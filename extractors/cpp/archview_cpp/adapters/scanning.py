"""Declarations and includes, read from the token stream.

The reader used when clang is not installed. It works on tokens, never on
lines of text, so a brace in a string and a keyword in a comment are not
mistaken for code. What it cannot see it says out loud: a declaration
written inside a macro is invisible to it, because a preprocessor line is
not C++ and is not read as if it were.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from ..domain.model import Declaration, DeclarationKind, SourcePath, Span
from .tokens import Token, TokenKind, TokenStream, tokenise

_TYPE_KEYWORDS = {
    "class": DeclarationKind.CLASS,
    "struct": DeclarationKind.STRUCT,
    "union": DeclarationKind.UNION,
    "enum": DeclarationKind.ENUM,
}

# Words that open a statement, not a declaration. `if (x) { … }` has the
# shape of a function definition to anything that only looks for a name, a
# parameter list and a brace.
_STATEMENTS = {
    "if", "for", "while", "switch", "catch", "return", "sizeof", "do", "else",
    "case", "throw", "new", "delete", "and", "or", "not", "alignof", "decltype",
    "static_assert", "noexcept", "typeid", "assert",
}

# What may stand between a parameter list and the body of a function.
_AFTER_PARAMETERS = {
    "const", "noexcept", "override", "final", "mutable", "volatile", "throw",
    "__declspec", "_NOEXCEPT",
}

_NOT_A_NAME = {
    "final",
    "sealed",
    "public",
    "private",
    "protected",
    "virtual",
    "class",
    "struct",
    "typename",
    "const",
    "friend",
    "typedef",
    "using",
    "return",
}

# Bases that say what a class is for, in the framework this kind of legacy
# repository is written against. A stereotype is read from the text, like
# everything else here — never assumed from a name.
_MFC_STEREOTYPES = {
    "CDialog": "dialog",
    "CDialogEx": "dialog",
    "CPropertyPage": "dialog",
    "CWinApp": "application",
    "CWinThread": "thread",
    "CView": "view",
    "CFormView": "view",
    "CListView": "view",
    "CTreeView": "view",
    "CScrollView": "view",
    "CDocument": "document",
    "CWnd": "window",
    "CFrameWnd": "window",
    "CMDIFrameWnd": "window",
    "CObject": "object",
    "CException": "exception",
}


@dataclass
class _Scope:
    """One open brace, and what it belongs to."""

    name: str | None
    is_type: bool
    declaration: "_Pending | None" = None


@dataclass
class _Pending:
    """A declaration whose body is open and whose end is not yet known."""

    name: str
    kind: DeclarationKind
    qualifier: str
    first: int
    bases: list[str] = field(default_factory=list)
    members: list[dict] = field(default_factory=list)


class TokenDeclarationReader:
    """Reads declarations from tokens."""

    def __init__(self, with_source: bool = True) -> None:
        self._with_source = with_source
        self.directives = 0
        self.unterminated = 0

    @property
    def describes(self) -> str:
        """Which reader this is."""
        return "this tool's own tokeniser"

    def read(self, path: SourcePath, text: str) -> list[Declaration]:
        """Every type defined in the file, nested ones included."""
        stream = tokenise(text)
        self.directives += stream.directives

        if stream.unterminated:
            self.unterminated += 1

        lines = text.splitlines()
        return _Walk(path, stream, lines, self._with_source).declarations()


class TokenIncludeReader:
    """Reads `#include "…"` from tokens, ignoring the angled form.

    An angled include names a library outside the repository — a compiler
    looks for it on the include path, not beside the file — so it can say
    nothing about a boundary between this repository's own modules.
    """

    def includes(self, path: SourcePath, text: str) -> list[str]:
        """The quoted include targets, in the order they are written."""
        found: list[str] = []

        for token in tokenise(text).tokens:
            if token.kind is not TokenKind.DIRECTIVE:
                continue

            body = token.text.lstrip()

            if not body.startswith("#"):
                continue

            body = body[1:].lstrip()

            if not body.startswith("include"):
                continue

            rest = body[len("include") :].lstrip()

            if not rest.startswith('"'):
                continue

            end = rest.find('"', 1)

            if end > 1:
                found.append(rest[1:end])

        return found


class _Walk:
    """One pass over one file's tokens."""

    def __init__(
        self,
        path: SourcePath,
        stream: TokenStream,
        lines: list[str],
        with_source: bool,
    ) -> None:
        self._path = path
        self._tokens = stream.tokens
        self._lines = lines
        self._with_source = with_source
        self._scopes: list[_Scope] = []
        self._found: list[Declaration] = []
        self._parentheses = 0

    def declarations(self) -> list[Declaration]:
        """Walks the tokens and returns what was declared."""
        index = 0
        template_line: int | None = None

        while index < len(self._tokens):
            token = self._tokens[index]

            if token.kind is TokenKind.PUNCTUATION:
                if token.text == "{":
                    self._open(token)
                    index += 1
                    continue

                if token.text == "}":
                    self._close(token)
                    index += 1
                    continue

                # A parameter list is not the class body. Counting what is
                # inside one made `SerialSensor(int address, …)` contribute
                # a member called `address`, which belongs to the method and
                # not to the type.
                if token.text == "(":
                    self._parentheses += 1
                elif token.text == ")":
                    self._parentheses = max(0, self._parentheses - 1)

            if token.kind is not TokenKind.IDENTIFIER:
                index += 1
                continue

            if token.text == "template":
                template_line = token.line
                index = self._past_angles(index + 1)
                continue

            if token.text == "namespace":
                index = self._namespace(index)
                template_line = None
                continue

            if token.text in _TYPE_KEYWORDS:
                index, opened = self._type(index, template_line)
                template_line = None
                continue

            # A function written at namespace scope is a thing the file
            # declares, exactly as a class is, and in C it is the only thing
            # a file declares at all.
            moved = self._function(index, template_line)

            if moved is not None:
                template_line = None
                index = moved
                continue

            self._member(index)
            template_line = None
            index += 1

        return self._found

    # -- scopes ---------------------------------------------------------

    def _open(self, token: Token) -> None:
        """A brace that no declaration claimed: an ordinary block."""
        self._scopes.append(_Scope(name=None, is_type=False))

    def _close(self, token: Token) -> None:
        if not self._scopes:
            return

        scope = self._scopes.pop()

        if scope.declaration is None:
            return

        pending = scope.declaration
        self._found.append(
            Declaration(
                name=pending.name,
                kind=pending.kind,
                path=self._path,
                span=Span(pending.first, max(token.line, pending.first)),
                qualifier=pending.qualifier,
                bases=tuple(pending.bases),
                members=tuple(
                    f"{m['text']}" for m in pending.members
                ),
                stereotype=_stereotype(pending),
                source=self._text(pending.first, token.line),
            )
        )

    @property
    def _qualifier(self) -> str:
        return "::".join(s.name for s in self._scopes if s.name)

    # -- declarations ---------------------------------------------------

    def _namespace(self, index: int) -> int:
        """`namespace A {`, `namespace A::B {`, or an anonymous one."""
        scan = index + 1
        parts: list[str] = []

        while scan < len(self._tokens):
            token = self._tokens[scan]

            if token.kind is TokenKind.IDENTIFIER:
                parts.append(token.text)
                scan += 1
                continue

            if token.kind is TokenKind.PUNCTUATION and token.text == ":":
                scan += 1
                continue

            break

        if scan < len(self._tokens) and self._tokens[scan].text == "{":
            self._scopes.append(
                _Scope(name="::".join(parts) if parts else None, is_type=False)
            )
            return scan + 1

        return scan

    def _type(self, index: int, template_line: int | None) -> tuple[int, bool]:
        """`class X : public Y {`, `enum class E {`, and the forms that are not."""
        keyword = self._tokens[index]
        kind = _TYPE_KEYWORDS[keyword.text]
        scan = index + 1
        name: str | None = None

        # `enum class E` and `enum struct E` name the kind twice.
        if kind is DeclarationKind.ENUM and scan < len(self._tokens):
            if self._tokens[scan].text in ("class", "struct"):
                scan += 1

        # Only what a class head may hold stands between the keyword and
        # the body: a name, a qualification, a template argument list, an
        # attribute in parentheses. Anything else means this is not a
        # definition at all.
        #
        # Skipping ahead to the next brace instead cost this reader nine
        # thousand phantom types on a legacy repository: `struct soap *soap`
        # in a parameter list looks like a class head to anyone willing to
        # ignore the `*`, and the brace it eventually reaches is the
        # function's body. The declarations were named after whatever
        # identifier stood last before it — `type`, `a`, `n`.
        while scan < len(self._tokens):
            token = self._tokens[scan]

            if token.kind is TokenKind.IDENTIFIER:
                if token.text not in _NOT_A_NAME:
                    name = token.text
                scan += 1
                continue

            if token.kind is not TokenKind.PUNCTUATION:
                return index + 1, False

            if token.text in (":", "{", ";"):
                break

            if token.text in ("<", ">"):
                scan += 1
                continue

            if token.text == "(":
                # An attribute or an export macro — `__declspec(dllexport)`.
                # It is not the name, so whatever was taken for one is not
                # the name either.
                scan = _past_parentheses(self._tokens, scan)
                name = None
                continue

            return index + 1, False

        if scan >= len(self._tokens) or name is None:
            return scan, False

        bases: list[str] = []
        token = self._tokens[scan]

        if token.text == ":":
            scan, bases = self._bases(scan + 1)

        if scan >= len(self._tokens) or self._tokens[scan].text != "{":
            # A forward declaration, or a variable of an elaborated type.
            # Neither declares a body, and neither is a definition to draw.
            return scan, False

        self._scopes.append(
            _Scope(
                name=name,
                is_type=True,
                declaration=_Pending(
                    name=name,
                    kind=kind,
                    qualifier=self._qualifier,
                    first=template_line or keyword.line,
                    bases=bases,
                ),
            )
        )

        return scan + 1, True

    def _function(self, index: int, template_line: int | None) -> int | None:
        """A function defined at namespace scope: `int add(int a, int b) { … }`.

        Only a definition, never a prototype, for the same reason only a class
        with a body is drawn: a name promised elsewhere is not a thing this
        file holds.

        A qualified name — `Sensor::read(…)` — defines a member out of line.
        It belongs to its type, which already lists it, and drawing it again
        beside the type would put one member on the map twice.
        """
        if any(scope.is_type for scope in self._scopes):
            return None

        token = self._tokens[index]

        if token.text in _STATEMENTS or token.text in _NOT_A_NAME:
            return None

        if index + 1 >= len(self._tokens) or self._tokens[index + 1].text != "(":
            return None

        previous = self._tokens[index - 1] if index else None

        if previous is not None and previous.text in (":", ".", "~", "#"):
            return None

        if not self._opens_declaration(index):
            return None

        after = _past_parentheses(self._tokens, index + 1)
        scan = after

        while scan < len(self._tokens):
            following = self._tokens[scan]

            if following.kind is TokenKind.IDENTIFIER and following.text in _AFTER_PARAMETERS:
                scan += 1
                continue

            if following.kind is TokenKind.PUNCTUATION and following.text in ("-", ">", "&", "*"):
                scan += 1
                continue

            break

        if scan >= len(self._tokens) or self._tokens[scan].text != "{":
            return None

        first = template_line or self._return_line(index)
        end = self._closing(scan)

        if end is None:
            return None

        self._found.append(
            Declaration(
                name=self._tokens[index].text,
                kind=DeclarationKind.FUNCTION,
                path=self._path,
                span=Span(first, max(end, first)),
                qualifier=self._qualifier,
                stereotype="function",
                source=self._text(first, end),
            )
        )

        return end_index(self._tokens, scan)

    def _opens_declaration(self, index: int) -> bool:
        """Whether a declaration can begin where this name's head would begin.

        A name, a parameter list and a brace are also the shape of an entry in
        a constructor's initialiser list — `: Sensor(address), _port(port) {}`
        — and of a good deal else. What distinguishes a definition is what
        stands before its return type: the end of the previous statement, and
        nothing else.
        """
        scan = index - 1

        while scan >= 0:
            token = self._tokens[scan]

            if token.kind is TokenKind.DIRECTIVE:
                return True

            if token.kind is TokenKind.PUNCTUATION:
                if token.text in (";", "}", "{"):
                    return True

                if token.text in ("*", "&", ":", "<", ">", "[", "]"):
                    scan -= 1
                    continue

                return False

            if token.kind is TokenKind.IDENTIFIER:
                if token.text in _STATEMENTS:
                    return False

                scan -= 1
                continue

            return False

        return True

    def _return_line(self, index: int) -> int:
        """The line the declaration starts on, return type included."""
        line = self._tokens[index].line
        scan = index - 1

        while scan >= 0 and self._tokens[scan].line >= line - 2:
            token = self._tokens[scan]

            if token.kind is TokenKind.PUNCTUATION and token.text in (";", "}", "{"):
                break

            if token.kind is TokenKind.DIRECTIVE:
                break

            line = min(line, token.line)
            scan -= 1

        return line

    def _closing(self, brace: int) -> int | None:
        """The line of the brace closing the body that opens at that token."""
        depth = 0

        for scan in range(brace, len(self._tokens)):
            text = self._tokens[scan].text

            if text == "{":
                depth += 1
            elif text == "}":
                depth -= 1

                if depth == 0:
                    return self._tokens[scan].line

        return None

    def _bases(self, index: int) -> tuple[int, list[str]]:
        """The base list, reduced to the name of each base."""
        bases: list[str] = []
        current: str | None = None
        depth = 0
        scan = index

        while scan < len(self._tokens):
            token = self._tokens[scan]

            if token.kind is TokenKind.PUNCTUATION:
                if token.text == "<":
                    depth += 1
                elif token.text == ">":
                    depth = max(0, depth - 1)
                elif token.text == "," and depth == 0:
                    if current:
                        bases.append(current)
                    current = None
                elif token.text == "{" and depth == 0:
                    break

                scan += 1
                continue

            if token.kind is TokenKind.IDENTIFIER and depth == 0:
                if token.text not in _NOT_A_NAME:
                    current = token.text

            scan += 1

        if current:
            bases.append(current)

        return scan, bases

    def _member(self, index: int) -> None:
        """A name written directly inside a type body.

        Only what the tokens support: an identifier that opens a parameter
        list is a method, one that ends a statement is a field. A type's
        members are shown as its own text shows them.
        """
        if self._parentheses or not self._scopes or not self._scopes[-1].is_type:
            return

        pending = self._scopes[-1].declaration

        if pending is None or len(pending.members) >= 60:
            return

        token = self._tokens[index]

        if token.text in _NOT_A_NAME or token.text in _TYPE_KEYWORDS:
            return

        following = self._tokens[index + 1] if index + 1 < len(self._tokens) else None

        if following is None or following.kind is not TokenKind.PUNCTUATION:
            return

        if following.text == "(":
            pending.members.append({"text": f"{token.text}(…)", "line": token.line})
            return

        if following.text in (";", "=", "[", ","):
            # A field is named after its type, with pointers, references
            # and qualifiers between the two; a bare identifier ending a
            # statement is usually an enumerator, which is a member too.
            written = self._type_before(index)

            pending.members.append(
                {
                    "text": f"{written} {token.text}" if written else token.text,
                    "line": token.line,
                }
            )

    def _type_before(self, index: int) -> str:
        """The type written before a field's name, as the source writes it."""
        parts: list[str] = []
        scan = index - 1

        while scan >= 0 and len(parts) < 6:
            token = self._tokens[scan]

            if token.kind is TokenKind.IDENTIFIER:
                if token.text in ("public", "private", "protected"):
                    break

                parts.append(token.text)
                scan -= 1
                continue

            if token.kind is TokenKind.PUNCTUATION and token.text in ("*", "&", "<", ">"):
                parts.append(token.text)
                scan -= 1
                continue

            # A single colon ends an access specifier — `private:` — and is
            # not part of the type. A doubled one qualifies a name and is.
            if token.kind is TokenKind.PUNCTUATION and token.text == ":":
                if scan > 0 and self._tokens[scan - 1].text == ":":
                    parts.append("::")
                    scan -= 2
                    continue

                break

            break

        return " ".join(reversed(parts)).replace(" :: ", "::").strip()

    # -- helpers --------------------------------------------------------

    def _past_angles(self, index: int) -> int:
        """Past a `template <…>` parameter list, nesting respected."""
        if index >= len(self._tokens) or self._tokens[index].text != "<":
            return index

        depth = 0
        scan = index

        while scan < len(self._tokens):
            text = self._tokens[scan].text

            if text == "<":
                depth += 1
            elif text == ">":
                depth -= 1

                if depth == 0:
                    return scan + 1

            scan += 1

        return scan

    def _text(self, first: int, last: int) -> str | None:
        """The declaration as written, cut when longer than anyone reads."""
        if not self._with_source:
            return None

        limit = 400
        taken = self._lines[first - 1 : first - 1 + min(last - first + 1, limit)]
        text = "\n".join(taken)

        if last - first + 1 > limit:
            text += f"\n… {last - first + 1 - limit} more lines"

        return text


def end_index(tokens, brace: int) -> int:
    """The token after the brace that closes the body opening at `brace`."""
    depth = 0

    for scan in range(brace, len(tokens)):
        text = tokens[scan].text

        if text == "{":
            depth += 1
        elif text == "}":
            depth -= 1

            if depth == 0:
                return scan + 1

    return len(tokens)


def _past_parentheses(tokens, index: int) -> int:
    """Past a balanced parenthesised group."""
    depth = 0
    scan = index

    while scan < len(tokens):
        text = tokens[scan].text

        if text == "(":
            depth += 1
        elif text == ")":
            depth -= 1

            if depth == 0:
                return scan + 1

        scan += 1

    return scan


def _stereotype(pending: _Pending) -> str | None:
    """What a type is for, when its bases say so."""
    for base in pending.bases:
        if base in _MFC_STEREOTYPES:
            return _MFC_STEREOTYPES[base]

    return pending.kind.value

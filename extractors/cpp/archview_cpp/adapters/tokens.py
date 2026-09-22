"""One pass over C and C++ text, turning it into tokens.

This repository forbids regular expressions as parsers, for a reason worth
repeating: they mishandle the hard cases and fail **silently**, so the graph
comes out wrong and nothing says so. A brace inside a string is a brace to a
regex; a keyword inside a comment is a keyword.

So the text is tokenised the way the language reads it — comments, string,
character and raw-string literals, and preprocessor lines all recognised as
what they are — and everything above this module works on tokens, never on
lines of text.

What it does not do is expand macros. A declaration written inside one is
invisible here, and the run says how many preprocessor lines it passed over
rather than pretending they held nothing.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum

_PUNCTUATION = set("{}()[]<>;:,.*&=+-/%!~^|?#@$")


class TokenKind(str, Enum):
    """What a token is. Only the distinctions this tool acts on."""

    IDENTIFIER = "identifier"
    PUNCTUATION = "punctuation"
    NUMBER = "number"
    STRING = "string"
    CHARACTER = "character"
    DIRECTIVE = "directive"


@dataclass(frozen=True)
class Token:
    """One token, and the line it began on."""

    kind: TokenKind
    text: str
    line: int


@dataclass(frozen=True)
class TokenStream:
    """The tokens of one file, with what was skipped to produce them."""

    tokens: tuple[Token, ...]
    directives: int
    comments: int
    unterminated: bool

    def __len__(self) -> int:
        return len(self.tokens)


def tokenise(text: str) -> TokenStream:
    """Reads C or C++ text into tokens.

    Never raises: text this cannot read is text the tool must still report
    on. An unterminated literal or comment sets a flag and ends the file.
    """
    tokens: list[Token] = []
    directives = 0
    comments = 0
    unterminated = False

    length = len(text)
    index = 0
    line = 1

    while index < length:
        character = text[index]

        if character == "\n":
            line += 1
            index += 1
            continue

        if character in " \t\r\v\f":
            index += 1
            continue

        # Comments. A block comment may span lines and may hold anything.
        if character == "/" and index + 1 < length:
            following = text[index + 1]

            if following == "/":
                index = _through_line(text, index)
                comments += 1
                continue

            if following == "*":
                end = text.find("*/", index + 2)
                comments += 1

                if end < 0:
                    unterminated = True
                    break

                line += text.count("\n", index, end)
                index = end + 2
                continue

        # A preprocessor line. Taken whole, continuations included: what it
        # contains is not this language, and reading it as if it were is how
        # a macro definition becomes a phantom declaration.
        if character == "#" and _opens_line(text, index):
            start = index
            index = _through_directive(text, index)
            body = text[start:index]
            tokens.append(Token(TokenKind.DIRECTIVE, body, line))
            line += body.count("\n")
            directives += 1
            continue

        # A raw string: R"delimiter( anything at all )delimiter"
        if character in "Ru8LU" and _opens_raw_string(text, index):
            start = index
            index, line, closed = _through_raw_string(text, index, line)
            tokens.append(Token(TokenKind.STRING, text[start:index], line))

            if not closed:
                unterminated = True
                break

            continue

        if character == '"' or character == "'":
            start = index
            index, line, closed = _through_quoted(text, index, line)
            kind = TokenKind.STRING if character == '"' else TokenKind.CHARACTER
            tokens.append(Token(kind, text[start:index], line))

            if not closed:
                unterminated = True
                break

            continue

        if character.isdigit():
            start = index

            while index < length and (
                text[index].isalnum() or text[index] in "._"
            ):
                index += 1

            tokens.append(Token(TokenKind.NUMBER, text[start:index], line))
            continue

        if character.isalpha() or character == "_":
            start = index

            while index < length and (text[index].isalnum() or text[index] == "_"):
                index += 1

            tokens.append(Token(TokenKind.IDENTIFIER, text[start:index], line))
            continue

        if character in _PUNCTUATION:
            tokens.append(Token(TokenKind.PUNCTUATION, character, line))
            index += 1
            continue

        index += 1

    return TokenStream(tuple(tokens), directives, comments, unterminated)


def _opens_line(text: str, index: int) -> bool:
    """Whether only blank space stands between this and the line's start."""
    back = index - 1

    while back >= 0 and text[back] in " \t":
        back -= 1

    return back < 0 or text[back] == "\n"


def _through_line(text: str, index: int) -> int:
    """Past a line comment, which a backslash may continue onto the next."""
    while index < len(text):
        if text[index] == "\n" and not _continued(text, index):
            return index
        index += 1

    return len(text)


def _through_directive(text: str, index: int) -> int:
    """Past a whole preprocessor line, continuations included."""
    while index < len(text):
        if text[index] == "\n" and not _continued(text, index):
            return index

        if text[index] == "/" and index + 1 < len(text) and text[index + 1] == "*":
            end = text.find("*/", index + 2)
            index = len(text) if end < 0 else end + 2
            continue

        index += 1

    return len(text)


def _continued(text: str, newline: int) -> bool:
    """Whether a backslash joins this line to the next."""
    back = newline - 1

    while back >= 0 and text[back] in " \t\r":
        back -= 1

    return back >= 0 and text[back] == "\\"


def _opens_raw_string(text: str, index: int) -> bool:
    """Whether an R"( … )" literal begins here, prefixes allowed."""
    scan = index

    while scan < len(text) and text[scan] in "u8LU":
        scan += 1

    return (
        scan < len(text)
        and text[scan] == "R"
        and scan + 1 < len(text)
        and text[scan + 1] == '"'
    )


def _through_raw_string(text: str, index: int, line: int) -> tuple[int, int, bool]:
    """Past a raw string, whose delimiter says where it ends."""
    quote = text.index('"', index)
    open_paren = text.find("(", quote)

    if open_paren < 0:
        return len(text), line, False

    closing = ")" + text[quote + 1 : open_paren] + '"'
    end = text.find(closing, open_paren)

    if end < 0:
        return len(text), line + text.count("\n", index), False

    stop = end + len(closing)
    return stop, line + text.count("\n", index, stop), True


def _through_quoted(text: str, index: int, line: int) -> tuple[int, int, bool]:
    """Past a string or character literal, escapes respected."""
    quote = text[index]
    scan = index + 1

    while scan < len(text):
        character = text[scan]

        if character == "\\":
            if scan + 1 < len(text) and text[scan + 1] == "\n":
                line += 1
            scan += 2
            continue

        if character == quote:
            return scan + 1, line, True

        # An unterminated literal ends at the line's end, as the language
        # says it does. Reading on would swallow the rest of the file.
        if character == "\n":
            return scan + 1, line + 1, False

        scan += 1

    return len(text), line, False

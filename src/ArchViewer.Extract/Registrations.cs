using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ArchViewer.Extract;

/// <summary>
/// Types named as arguments of a generic call — which is how a composition
/// root says what it wires to what.
/// <para>
/// Reading what a type inherits, implements and holds cannot see
/// <c>AddScoped&lt;IThing, Thing&gt;()</c>: the two types appear nowhere in
/// the shape of the registering class, only in the instruction that registers
/// them. For an application whose wiring lives in such calls, that is the
/// main mechanism of assembly, and a map without it shows the parts and not
/// the assembly.
/// </para>
/// <para>
/// This is not reading a method's logic. A generic argument is a type named
/// in metadata; the instruction is where it is named, and nothing about the
/// method's behaviour is interpreted.
/// </para>
/// </summary>
internal static class Registrations
{
    private const byte Call = 0x28;
    private const byte CallVirtual = 0x6F;
    private const byte NewObject = 0x73;
    private const byte LongForm = 0xFE;

    /// <summary>
    /// Reads one assembly and returns, for each type, the types it names as
    /// generic arguments of the calls it makes.
    /// </summary>
    /// <param name="assemblyPath">Assembly to read.</param>
    /// <param name="known">Full names worth recording.</param>
    /// <returns>Pairs of declaring type and named type.</returns>
    public static IReadOnlyList<(string From, string To)> Read(
        string assemblyPath,
        ISet<string> known)
    {
        var found = new HashSet<(string, string)>();

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata)
            {
                return Array.Empty<(string, string)>();
            }

            var reader = pe.GetMetadataReader();
            var provider = new TypeNames();

            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var owner = FullName(reader, type);

                if (owner is null || !known.Contains(owner))
                {
                    continue;
                }

                foreach (var methodHandle in type.GetMethods())
                {
                    foreach (var named in Named(pe, reader, provider, methodHandle, known))
                    {
                        if (named != owner)
                        {
                            found.Add((owner, named));
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is BadImageFormatException or IOException
                                      or InvalidOperationException)
        {
            return Array.Empty<(string, string)>();
        }

        return found.ToList();
    }

    private static IEnumerable<string> Named(
        PEReader pe,
        MetadataReader reader,
        TypeNames provider,
        MethodDefinitionHandle handle,
        ISet<string> known)
    {
        var method = reader.GetMethodDefinition(handle);

        if (method.RelativeVirtualAddress == 0)
        {
            yield break;
        }

        MethodBodyBlock body;

        try
        {
            body = pe.GetMethodBody(method.RelativeVirtualAddress);
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException)
        {
            yield break;
        }

        foreach (var token in Calls(body.GetILReader()))
        {
            var entity = MetadataTokens.EntityHandle(token);

            if (entity.Kind != HandleKind.MethodSpecification)
            {
                continue;
            }

            ImmutableArray<string> arguments;

            try
            {
                var spec = reader.GetMethodSpecification((MethodSpecificationHandle)entity);
                arguments = spec.DecodeSignature(provider, genericContext: null);
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException
                                          or NotSupportedException)
            {
                continue;
            }

            foreach (var argument in arguments)
            {
                // Every type the argument names, not only the outermost. A
                // call may hand over a type of this repository wrapped in a
                // collection, a task, a result — at any depth — and the
                // wrapper is the framework's while the contents are ours.
                foreach (var named in Mentioned(argument))
                {
                    if (known.Contains(named))
                    {
                        yield return named;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every type named inside a decoded type name, the wrapper included.
    /// </summary>
    /// <param name="name">A name such as Outer&lt;Inner&lt;Leaf&gt;,Other&gt;.</param>
    /// <returns>Each name it mentions, outermost first.</returns>
    private static IEnumerable<string> Mentioned(string name)
    {
        var start = 0;

        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] is not ('<' or '>' or ','))
            {
                continue;
            }

            if (i > start)
            {
                yield return name[start..i];
            }

            start = i + 1;
        }
    }

    /// <summary>
    /// Tokens of the calls a body makes.
    /// Every instruction must be stepped over by its true length: guessing at
    /// the stride would read an operand as an opcode and invent calls that
    /// were never written.
    /// </summary>
    private static IEnumerable<int> Calls(BlobReader il)
    {
        var tokens = new List<int>();

        while (il.RemainingBytes > 0)
        {
            var opcode = il.ReadByte();

            if (opcode == LongForm)
            {
                if (il.RemainingBytes == 0)
                {
                    break;
                }

                var second = il.ReadByte();
                var wide = Operands.Long(second);

                if (wide < 0 || il.RemainingBytes < wide)
                {
                    break;
                }

                il.Offset += wide;
                continue;
            }

            if (opcode == 0x45)
            {
                // A switch carries a count and then that many offsets.
                if (il.RemainingBytes < 4)
                {
                    break;
                }

                var count = il.ReadUInt32();
                var bytes = checked((int)(count * 4));

                if (il.RemainingBytes < bytes)
                {
                    break;
                }

                il.Offset += bytes;
                continue;
            }

            var operand = Operands.Short(opcode);

            if (operand < 0 || il.RemainingBytes < operand)
            {
                break;
            }

            if (operand == 4 && opcode is Call or CallVirtual or NewObject)
            {
                tokens.Add(il.ReadInt32());
                continue;
            }

            il.Offset += operand;
        }

        return tokens;
    }

    private static string? FullName(MetadataReader reader, TypeDefinition type)
    {
        try
        {
            var name = reader.GetString(type.Name);
            var space = reader.GetString(type.Namespace);
            var bare = name.Split('`')[0];
            return space.Length == 0 ? bare : $"{space}.{bare}";
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// How many bytes follow each instruction. Stepping by the wrong amount reads
/// an operand as an opcode, and the result is calls that were never written —
/// so the table is stated rather than guessed.
/// </summary>
internal static partial class Operands
{
    /// <summary>Operand size of a one-byte opcode, or -1 when unknown.</summary>
    public static int Short(byte opcode) => opcode switch
    {
        // No operand: arithmetic, stack and short-form loads and stores.
        <= 0x0E or 0x14 or (>= 0x16 and <= 0x1E) or (>= 0x25 and <= 0x26)
            or (>= 0x2A and <= 0x2A) or (>= 0x46 and <= 0x4E) or (>= 0x50 and <= 0x5F)
            or (>= 0x61 and <= 0x66) or (>= 0x67 and <= 0x6C) or (>= 0x6D and <= 0x6E)
            or (>= 0xA4 and <= 0xA5) or (>= 0xB3 and <= 0xB7) or (>= 0xB9 and <= 0xBA)
            or (>= 0xC3 and <= 0xC3) or 0xD1 or (>= 0xD3 and <= 0xDC) or 0xDD or 0xDE
            or (>= 0xE0 and <= 0xE0) => 0,

        // One byte: short branches, short indexes, ldc.i4.s.
        0x0F or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F
            or (>= 0x2B and <= 0x37) or 0xDF => 1,

        // Four bytes: tokens, long branches, ldc.i4, ldc.r4.
        0x20 or 0x22 or (>= 0x28 and <= 0x29) or (>= 0x38 and <= 0x44)
            or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79
            or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81
            or 0x8C or 0x8D or 0x8F or 0xA3 or 0xA5 or 0xC2 or 0xC6
            or (>= 0xD0 and <= 0xD0) => 4,

        // Eight bytes: ldc.i8, ldc.r8.
        0x21 or 0x23 => 8,

        _ => -1,
    };

    /// <summary>Operand size of a two-byte opcode after the 0xFE prefix.</summary>
    public static int Long(byte second) => second switch
    {
        <= 0x05 or (>= 0x0A and <= 0x0B) or 0x0D or (>= 0x14 and <= 0x1E) => 0,
        0x06 or 0x07 or 0x09 or 0x0C or 0x0E or 0x0F or 0x10 or 0x11
            or 0x12 or 0x13 or 0x15 or 0x16 or 0x1C => 4,
        _ => -1,
    };
}

/// <summary>
/// Reads a signature into the full names of the types it mentions. Only the
/// name matters here: the model records types by name, and a constructed
/// generic is recorded as its definition.
/// </summary>
internal sealed class TypeNames : ISignatureTypeProvider<string, object?>
{
    /// <summary>A type defined in this assembly.</summary>
    public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(type.Name).Split('`')[0];
        var space = metadata.GetString(type.Namespace);
        return space.Length == 0 ? name : $"{space}.{name}";
    }

    /// <summary>A type defined elsewhere and referenced here.</summary>
    public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = metadata.GetTypeReference(handle);
        var name = metadata.GetString(type.Name).Split('`')[0];
        var space = metadata.GetString(type.Namespace);
        return space.Length == 0 ? name : $"{space}.{name}";
    }

    /// <summary>A constructed type, read through its own signature.</summary>
    public string GetTypeFromSpecification(
        MetadataReader metadata,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    /// <summary>
    /// A constructed generic, named with the arguments it was given.
    /// <para>
    /// Naming it by its definition alone threw the arguments away, and with
    /// them the only part that says anything about this repository:
    /// <c>.Produces&lt;IReadOnlyList&lt;ActiveGrantView&gt;&gt;()</c> came
    /// back as <c>IReadOnlyList</c>, which belongs to the framework and
    /// matches nothing here, so the reference to ActiveGrantView was not
    /// recorded at all. The same call written without the collection was.
    /// </para>
    /// </summary>
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        typeArguments.IsDefaultOrEmpty
            ? genericType
            : $"{genericType}<{string.Join(",", typeArguments)}>";

    /// <summary>An array is named by what it holds.</summary>
    public string GetSZArrayType(string elementType) => elementType;

    /// <summary>A multi-dimensional array, likewise.</summary>
    public string GetArrayType(string elementType, ArrayShape shape) => elementType;

    /// <summary>A pointer, likewise.</summary>
    public string GetPointerType(string elementType) => elementType;

    /// <summary>A by-reference type, likewise.</summary>
    public string GetByReferenceType(string elementType) => elementType;

    /// <summary>A pinned type, likewise.</summary>
    public string GetPinnedType(string elementType) => elementType;

    /// <summary>A modifier does not change what type this is.</summary>
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    /// <summary>A primitive, by its framework name.</summary>
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

    /// <summary>A method's own type parameter: no name of its own here.</summary>
    public string GetGenericMethodParameter(object? genericContext, int index) => "";

    /// <summary>A type's own parameter, likewise.</summary>
    public string GetGenericTypeParameter(object? genericContext, int index) => "";

    /// <summary>A function pointer names nothing this map records.</summary>
    public string GetFunctionPointerType(MethodSignature<string> signature) => "";
}

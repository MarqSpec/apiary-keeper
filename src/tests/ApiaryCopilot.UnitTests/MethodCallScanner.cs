using System.Reflection;
using System.Reflection.Emit;

namespace ApiaryCopilot.UnitTests;

/// <summary>
/// Finds every method whose IL calls, or takes a token of, a method with a given name. Reads the loaded
/// assemblies' IL, so it sees lambdas, async state machines and expression trees, and needs no file I/O.
/// </summary>
internal static class MethodCallScanner
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    public static ScanResult FindCallsTo(string methodName, IEnumerable<Assembly> assemblies)
    {
        var callers = new List<string>();
        var scanned = new Dictionary<string, int>();
        foreach (var assembly in assemblies)
        {
            var count = 0;
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
                {
                    var il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il is null)
                    {
                        continue;
                    }

                    count++;
                    if (ReferencedMethods(method, il).Any(m => m.Name == methodName))
                    {
                        callers.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }

            scanned[assembly.GetName().Name!] = count;
        }

        return new ScanResult(scanned, callers);
    }

    private static IEnumerable<MethodBase> ReferencedMethods(MethodBase method, byte[] il)
    {
        var typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            var value = (short)il[position++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[position++]);
            }

            var opCode = OpCodesByValue[value];
            switch (opCode.OperandType)
            {
                case OperandType.InlineMethod:
                    yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArgs, methodArgs)!;
                    break;
                case OperandType.InlineTok:
                    // ldtoken of a method is how an expression tree refers to it.
                    if (method.Module.ResolveMember(BitConverter.ToInt32(il, position), typeArgs, methodArgs) is MethodBase m)
                    {
                        yield return m;
                    }

                    break;
            }

            position += OperandSize(opCode, il, position);
        }
    }

    private static int OperandSize(OpCode opCode, byte[] il, int position) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => 4,
    };
}

internal sealed record ScanResult(IReadOnlyDictionary<string, int> MethodsScannedByAssembly, IReadOnlyList<string> Callers);

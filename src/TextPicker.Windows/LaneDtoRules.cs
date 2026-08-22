using System.Reflection;

namespace TextPicker.Windows;

/// <summary>ADR-0003 结构规则：跨 lane DTO（<see cref="ILaneTransferable"/> 实现）不得携带 COM 接口类型成员。
/// 契约测试 #6 据此扫描；COM 接口即 CsWin32 生成的 [ComImport] 接口（如 IUIAutomation*）。</summary>
internal static class LaneDtoRules
{
    public static bool IsComInterface(Type type)
    {
        if (!type.IsInterface)
        {
            return false;
        }

        return type.IsDefined(ComImportAttributeType, inherit: false);
    }

    private static readonly Type ComImportAttributeType = typeof(System.Runtime.InteropServices.ComImportAttribute);

    /// <summary>返回程序集中所有违反「COM 不过 lane」规则的 (声明类型, 成员名, COM 类型)。</summary>
    public static IReadOnlyList<(Type DeclaringType, string Member, Type ComType)> FindViolations(Assembly assembly)
    {
        var violations = new List<(Type, string, Type)>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsInterface || !typeof(ILaneTransferable).IsAssignableFrom(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (IsComInterface(property.PropertyType))
                {
                    violations.Add((type, property.Name, property.PropertyType));
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (IsComInterface(field.FieldType))
                {
                    violations.Add((type, field.Name, field.FieldType));
                }
            }
        }

        return violations;
    }
}

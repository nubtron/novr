using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace NOVR;

/// <summary>
/// Marks a static class that owns part of the mod's configuration: it must
/// expose <c>public static void Bind(ConfigFile config)</c>, and that method is
/// called once at startup without anything having to name the class.
///
/// The point is not tidiness. This mod is developed as a stack of independent
/// branches that are rebased, never merged, and every layer that added a setting
/// also had to append to the shared ModConfiguration — so every layer conflicted
/// with every other layer on the same three lists, on every restack. A layer
/// that instead drops in one new file touches nothing another layer touches, and
/// the conflict cannot occur rather than being resolved cheaply.
///
/// <para><c>Order</c> only decides the order sections are bound in, which is the
/// order BepInEx writes them to the .cfg. It has no effect on behaviour; it
/// exists so the generated file is stable rather than dependent on the order
/// reflection happens to return types in.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ConfigSectionAttribute : Attribute
{
    public int Order { get; set; }
}

/// <summary>
/// Finds every <see cref="ConfigSectionAttribute"/> class in this assembly and
/// binds it.
/// </summary>
public static class ConfigSections
{
    public static void BindAll(ConfigFile config)
    {
        foreach (var (type, bind) in Discover())
        {
            try
            {
                bind.Invoke(null, new object[] { config });
            }
            catch (Exception e)
            {
                // One layer's broken section must not cost the pilot every other
                // setting in the mod. The entries it owns will fall back to their
                // defaults, which is a degraded mod rather than an unusable one —
                // and the log line is what turns "my setting does nothing" into a
                // five-second diagnosis.
                Debug.LogError($"[NOVR] Config section {type.FullName} failed to bind: {e.InnerException ?? e}");
            }
        }
    }

    private static IEnumerable<(Type type, MethodInfo bind)> Discover()
    {
        Type[] types;
        try
        {
            types = typeof(ConfigSections).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            // A type that cannot load takes the whole GetTypes() call with it,
            // and this fork has already shipped against a game version that
            // removed an API out from under it (see codex/game-api-compat). The
            // types that *did* load are still worth binding.
            types = e.Types.Where(t => t != null).ToArray();
            Debug.LogWarning(
                $"[NOVR] {e.Types.Length - types.Length} type(s) failed to load while scanning for config " +
                "sections; their settings will be missing. This usually means the game changed an API.");
        }

        var found = new List<(int order, string name, Type type, MethodInfo bind)>();
        foreach (var type in types)
        {
            var attribute = type.GetCustomAttribute<ConfigSectionAttribute>();
            if (attribute == null) continue;

            var bind = type.GetMethod(
                "Bind", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ConfigFile) }, null);
            if (bind == null)
            {
                // Loud, because the symptom otherwise is a setting that silently
                // does not exist — the most expensive kind of bug in this mod's
                // history is exactly that shape.
                Debug.LogError(
                    $"[NOVR] [ConfigSection] {type.FullName} has no 'public static void Bind(ConfigFile)' — " +
                    "its settings will not exist.");
                continue;
            }

            found.Add((attribute.Order, type.FullName, type, bind));
        }

        // Deterministic: reflection order is not part of any contract, and an
        // unstable order would reshuffle the pilot's .cfg on every build.
        found.Sort((a, b) => a.order != b.order
            ? a.order.CompareTo(b.order)
            : string.CompareOrdinal(a.name, b.name));

        return found.Select(f => (f.type, f.bind));
    }
}

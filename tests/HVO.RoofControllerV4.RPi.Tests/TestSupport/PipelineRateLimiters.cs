using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.RateLimiting;
using HVO.RoofControllerV4.RPi;
using Microsoft.AspNetCore.Http;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Finds the rate limiters a host's request pipeline made, so a test host can dispose them when it stops. ASP.NET Core's
/// rate-limiting middleware never disposes its limiters, and each keeps a heartbeat timer whose callback holds the
/// middleware, and through it the whole pipeline and service provider. The production process has one host for its
/// whole life, so nothing is lost there; but every host a test stops would stay in the heap (about 1 MB each), and a
/// soak that restarts the controller would measure that as a leak.
/// </summary>
/// <remarks>
/// The walk follows instance fields only through ASP.NET Core, Microsoft.Extensions and the controller's own objects,
/// collections and delegates, so it stays inside the host: it never enters threads, timers or execution contexts, which lead to every
/// other object in the process (another host's limiters among them).
/// </remarks>
internal static class PipelineRateLimiters
{
    private const int MaxObjects = 1_000_000;

    private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new();

    /// <summary>The <see cref="PartitionedRateLimiter{HttpContext}"/> instances reachable from <paramref name="server"/>.</summary>
    public static IReadOnlyList<PartitionedRateLimiter<HttpContext>> Find(object server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var found = new List<PartitionedRateLimiter<HttpContext>>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(server);
        while (pending.TryPop(out var item) && seen.Count < MaxObjects)
        {
            if (!seen.Add(item))
            {
                continue;
            }

            switch (item)
            {
                case PartitionedRateLimiter<HttpContext> limiter:
                    found.Add(limiter);
                    break;
                case Delegate callback:
                    foreach (var target in callback.GetInvocationList().Select(d => d.Target).OfType<object>())
                    {
                        pending.Push(target);
                    }

                    break;
                case Array array when Followed(array.GetType().GetElementType()!):
                    foreach (var element in array)
                    {
                        if (element is not null)
                        {
                            pending.Push(element);
                        }
                    }

                    break;
                case not Array when Followed(item.GetType()):
                    foreach (var field in FieldsOf(item.GetType()))
                    {
                        if (field.GetValue(item) is { } value)
                        {
                            pending.Push(value);
                        }
                    }

                    break;
            }
        }

        return found;
    }

    private static bool Followed(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string))
        {
            return false;
        }

        // An interface or object element type holds anything; each element is checked by its own type.
        if (type.IsInterface || type == typeof(object) || typeof(Delegate).IsAssignableFrom(type))
        {
            return true;
        }

        // The controller's own middleware (RequireHttpsMiddleware) sits in the pipeline between the framework's.
        var space = type.Namespace ?? string.Empty;
        return type.Assembly == typeof(Program).Assembly
            || space.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || space.StartsWith("Microsoft.Extensions", StringComparison.Ordinal)
            || space.StartsWith("System.Collections", StringComparison.Ordinal);
    }

    private static FieldInfo[] FieldsOf(Type type) => Fields.GetOrAdd(type, static type =>
    {
        var fields = new List<FieldInfo>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            fields.AddRange(current
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && !f.FieldType.IsPointer && !f.FieldType.IsFunctionPointer
                    && f.FieldType != typeof(string)));
        }

        return fields.ToArray();
    });
}

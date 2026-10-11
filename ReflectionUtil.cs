using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace CustomQuestGuide;

public static class ReflectionUtil
{
    /// <summary>
    /// Reads a public numeric property by any of the supplied candidate names.
    /// This is useful for Lumina-generated rows whose schema member names can
    /// change between game/schema versions.
    /// </summary>
    public static long? GetNumericProperty(object row, params string[] propertyNames)
    {
        var type = row.GetType();

        foreach (var propertyName in propertyNames)
        {
            var property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

            if (property is null)
                continue;

            try
            {
                var value = property.GetValue(row);
                if (value is null)
                    continue;

                return Convert.ToInt64(value);
            }
            catch
            {
                // Try the next candidate property.
            }
        }

        return null;
    }

    public static object? GetPropertyValue(object row, params string[] propertyNames)
    {
        var type = row.GetType();

        foreach (var propertyName in propertyNames)
        {
            var property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

            if (property is null)
                continue;

            try
            {
                return property.GetValue(row);
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    public static bool? GetBoolProperty(object row, params string[] propertyNames)
    {
        var value = GetPropertyValue(row, propertyNames);
        if (value is null)
            return null;

        if (value is bool b)
            return b;

        try
        {
            return Convert.ToInt64(value) != 0;
        }
        catch
        {
            return null;
        }
    }

    public static uint GetNestedRowId(object row, params string[] propertyNames)
    {
        var type = row.GetType();

        foreach (var propertyName in propertyNames)
        {
            var property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

            if (property is null)
                continue;

            var id = ExtractRowId(property.GetValue(row));
            if (id != 0)
                return id;
        }

        return 0;
    }

    public static uint ExtractRowId(object? value)
    {
        if (value is null)
            return 0;

        try
        {
            var rowIdProp = value.GetType().GetProperty("RowId");
            if (rowIdProp?.GetValue(value) is { } rowIdRaw)
                return Convert.ToUInt32(rowIdRaw);

            var valueProp = value.GetType().GetProperty("Value");
            var inner = valueProp?.GetValue(value);

            if (inner is not null)
            {
                var innerId = inner.GetType().GetProperty("RowId")?.GetValue(inner);
                if (innerId is not null)
                    return Convert.ToUInt32(innerId);
            }
        }
        catch
        {
            // ignored
        }

        return 0;
    }

    /// <summary>
    /// Recursively searches a generated Lumina row for references whose property
    /// names contain "Item". Used to index quest/achievement/shop sources without
    /// depending on one exact schema version.
    /// </summary>
    public static IEnumerable<uint> FindReferencedItemIds(
        object row,
        int maxDepth = 3)
    {
        var found = new HashSet<uint>();
        Walk(row, "", 0, maxDepth, found);
        return found;
    }

    private static void Walk(
        object? obj,
        string propertyPath,
        int depth,
        int maxDepth,
        HashSet<uint> found)
    {
        if (obj is null || depth > maxDepth)
            return;

        var type = obj.GetType();

        if (type.IsPrimitive || obj is string || obj is decimal)
            return;

        if (obj is IEnumerable enumerable && obj is not string)
        {
            foreach (var entry in enumerable)
                Walk(entry, propertyPath, depth + 1, maxDepth, found);

            return;
        }

        foreach (var property in type.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            object? value;

            try
            {
                value = property.GetValue(obj);
            }
            catch
            {
                continue;
            }

            var path = string.IsNullOrEmpty(propertyPath)
                ? property.Name
                : $"{propertyPath}.{property.Name}";

            if (property.Name.Contains("Item", StringComparison.OrdinalIgnoreCase))
            {
                var id = ExtractRowId(value);

                if (id != 0)
                    found.Add(id);
            }

            if (depth < maxDepth &&
                !property.PropertyType.IsPrimitive &&
                property.PropertyType != typeof(string))
            {
                Walk(value, path, depth + 1, maxDepth, found);
            }
        }
    }
}

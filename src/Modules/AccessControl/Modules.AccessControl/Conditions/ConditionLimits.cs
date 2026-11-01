using System.Text.Json.Nodes;

namespace FSH.Modules.AccessControl.Conditions;

/// <summary>
/// Size limits for grant conditions: at most 200 nodes (object properties and list items together), 8 levels of
/// <c>$and</c>/<c>$or</c>/<c>$not</c> and 100 values per list. Checked iteratively (no recursion) before anything
/// else walks the condition: at save time a condition over the limits is a 400, at use time it matches nothing.
/// </summary>
internal static class ConditionLimits
{
    public const int MaxNodes = 200;
    public const int MaxDepth = 8;
    public const int MaxListValues = 100;

    /// <summary>JSON nesting (objects and arrays); generous for 8 logical levels.</summary>
    public const int MaxJsonDepth = 40;

    /// <summary>Null when the condition is within the limits, otherwise why it is not.</summary>
    public static string? Check(JsonNode? condition)
    {
        int nodes = 0;
        var stack = new Stack<(JsonNode? Node, int Logical, int Json)>();
        stack.Push((condition, 0, 0));
        while (stack.Count > 0)
        {
            var (node, logical, json) = stack.Pop();
            if (logical > MaxDepth) return $"The condition is nested too deeply (at most {MaxDepth} levels of $and/$or/$not).";
            if (json > MaxJsonDepth) return "The condition is nested too deeply.";
            switch (node)
            {
                case JsonObject obj:
                    nodes += obj.Count;
                    foreach (var (key, value) in obj)
                        stack.Push((value, logical + (key is "$and" or "$or" or "$not" ? 1 : 0), json + 1));
                    break;
                case JsonArray array:
                    if (array.Count > MaxListValues) return $"A list in the condition may hold at most {MaxListValues} values.";
                    nodes += array.Count;
                    foreach (var item in array) stack.Push((item, logical, json + 1));
                    break;
            }
            if (nodes > MaxNodes) return $"The condition is too large (at most {MaxNodes} conditions and values).";
        }
        return null;
    }
}

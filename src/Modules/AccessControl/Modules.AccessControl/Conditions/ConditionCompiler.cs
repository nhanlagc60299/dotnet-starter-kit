using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FSH.Modules.AccessControl.Conditions;

/// <summary>The values of the condition variables for one request.</summary>
/// <param name="UserId">The value of <c>$CURRENT_USER</c>.</param>
/// <param name="Roles">The value of <c>$CURRENT_ROLES</c> (role names, as in the JWT role claims).</param>
internal sealed record ConditionVariables(Guid UserId, IReadOnlyList<string> Roles);

/// <summary>A condition that cannot be used; the message starts with the JSON path of the problem.</summary>
public sealed class InvalidConditionException : Exception
{
    public InvalidConditionException() { }
    public InvalidConditionException(string message) : base(message) { }
    public InvalidConditionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Compiles a grant condition (Strapi-style filter JSON) into a predicate body over an entity type, using only
/// operators EF Core translates to SQL. Paths are the entity's public scalar properties (string, Guid, bool,
/// numbers, DateTime, DateTimeOffset, enums and their nullables), matched case-insensitively; navigations are
/// not reachable. Values are passed as SQL parameters, not inlined.
/// </summary>
internal static partial class ConditionCompiler
{
    public const string CurrentUser = "$CURRENT_USER";
    public const string CurrentRoles = "$CURRENT_ROLES";

    private const string Root = "condition";
    private static readonly ConstantExpression True = Expression.Constant(true);
    private static readonly JsonSerializerOptions ValueOptions = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// Save-time check of the limits, operators, operand shapes and variables. Property names and value types can
    /// only be checked once the entity type is known (<see cref="TryBuild"/>), where a mismatch fails closed.
    /// </summary>
    /// <returns>Null when the condition is usable, otherwise the problem.</returns>
    public static string? Validate(JsonObject condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (ConditionLimits.Check(condition) is { } tooLarge) return tooLarge;
        try
        {
            new Walker(null, null).Object(condition, Root, 0);
            return null;
        }
        catch (InvalidConditionException e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// The predicate body over <paramref name="x"/> for stored condition JSON, or null (with the reason) when the
    /// condition cannot be used: unparsable, over the limits, an unknown property or a value of the wrong type.
    /// </summary>
    public static Expression? TryBuild(string json, ParameterExpression x, ConditionVariables variables, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(variables);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            problem = e.Message;
            return null;
        }
        if (node is not JsonObject condition)
        {
            problem = "The condition must be a JSON object.";
            return null;
        }
        problem = ConditionLimits.Check(condition);
        if (problem is not null) return null;
        try
        {
            return new Walker(x, variables).Object(condition, Root, 0);
        }
        catch (InvalidConditionException e)
        {
            problem = e.Message;
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // An expression the compiler failed to reject (a bug): still fail closed rather than throw on a read.
            problem = e.Message;
            return null;
        }
    }

    /// <summary>Like <see cref="TryBuild"/> but throws <see cref="InvalidConditionException"/>.</summary>
    public static Expression<Func<T, bool>> Build<T>(JsonObject condition, ConditionVariables variables)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var x = Expression.Parameter(typeof(T), "x");
        var body = TryBuild(condition.ToJsonString(), x, variables, out string? problem)
                   ?? throw new InvalidConditionException(problem!);
        return Expression.Lambda<Func<T, bool>>(body, x);
    }

    /// <summary>OR of the bodies; none matches nothing.</summary>
    public static Expression AnyOf(IReadOnlyList<Expression> bodies) =>
        bodies.Count == 0 ? Expression.Constant(false) : Balanced(bodies, Expression.OrElse);

    /// <summary>
    /// Combines expressions as a balanced tree (depth log n), never a left-deep chain: EF Core's visitors and
    /// <c>Compile()</c> recurse over the tree, so its depth must stay small.
    /// </summary>
    private static Expression Balanced(IReadOnlyList<Expression> items, Func<Expression, Expression, BinaryExpression> combine)
    {
        var level = items.ToList();
        while (level.Count > 1)
        {
            var next = new List<Expression>((level.Count + 1) / 2);
            for (int i = 0; i < level.Count; i += 2) next.Add(i + 1 < level.Count ? combine(level[i], level[i + 1]) : level[i]);
            level = next;
        }
        return level[0];
    }

    private static InvalidConditionException Invalid(string path, string message) => new($"{path}: {message}");

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z")]
    private static partial Regex PropertyName();

    /// <summary>
    /// Walks the condition. With no parameter (save time) it only checks the structure and returns <c>true</c>
    /// leaves; with one it builds the predicate. User-supplied keys only appear in error paths once validated.
    /// </summary>
    private sealed class Walker(ParameterExpression? x, ConditionVariables? variables)
    {
        public Expression Object(JsonObject obj, string path, int depth)
        {
            if (depth > ConditionLimits.MaxDepth) throw Invalid(path, "The condition is nested too deeply.");
            var items = new List<Expression>(obj.Count);
            foreach (var (key, value) in obj)
            {
                switch (key)
                {
                    case "$and" or "$or":
                        string p = $"{path}.{key}";
                        if (value is not JsonArray { Count: > 0 } array) throw Invalid(p, "Expected a non-empty list of conditions.");
                        var children = new List<Expression>(array.Count);
                        for (int i = 0; i < array.Count; i++)
                        {
                            children.Add(array[i] is JsonObject child
                                ? Object(child, $"{p}.{i}", depth + 1)
                                : throw Invalid($"{p}.{i}", "Expected a condition object."));
                        }
                        items.Add(Balanced(children, key == "$and" ? Expression.AndAlso : Expression.OrElse));
                        break;
                    case "$not":
                        if (value is not JsonObject inner) throw Invalid($"{path}.$not", "Expected a condition object.");
                        items.Add(Expression.Not(Object(inner, $"{path}.$not", depth + 1)));
                        break;
                    default:
                        items.Add(Field(key, value, path));
                        break;
                }
            }
            return items.Count == 0 ? True : Balanced(items, Expression.AndAlso);
        }

        private Expression Field(string name, JsonNode? value, string path)
        {
            if (name.StartsWith('$')) throw Invalid(path, "Unknown logical operator; use $and, $or or $not.");
            if (!PropertyName().IsMatch(name)) throw Invalid(path, "Invalid property name.");
            string p = $"{path}.{name}";
            var member = x is null ? null : Member(name, p);
            if (value is not JsonObject ops) return Op(member, "$eq", value, p);
            if (ops.Count == 0) throw Invalid(p, "Expected at least one operator.");
            var items = new List<Expression>(ops.Count);
            foreach (var (op, operand) in ops) items.Add(Op(member, op, operand, p));
            return Balanced(items, Expression.AndAlso);
        }

        private MemberExpression Member(string name, string path)
        {
            var matches = x!.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && IsScalar(p.PropertyType)
                            && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1) throw Invalid(path, "Unknown property.");
            return Expression.Property(x, matches[0]);
        }

        private Expression Op(MemberExpression? member, string op, JsonNode? operand, string path)
        {
            switch (op)
            {
                case "$eq" or "$ne":
                    // $CURRENT_ROLES is a list: equal to it means "one of".
                    return IsRolesVariable(operand)
                        ? In(member, RoleValues(), literal: false, negate: op == "$ne", $"{path}.{op}")
                        : Compare(member, op, operand, path);
                case "$gt" or "$gte" or "$lt" or "$lte":
                    if (member is not null && !IsOrderable(member.Type)) throw Invalid($"{path}.{op}", "Only numbers and dates can be ordered.");
                    return Compare(member, op, operand, path);
                case "$in" or "$nin":
                    string p = $"{path}.{op}";
                    if (IsRolesVariable(operand)) return In(member, RoleValues(), literal: false, negate: op == "$nin", p);
                    if (operand is not JsonArray { Count: > 0 } list) throw Invalid(p, "Expected a non-empty list of values, or $CURRENT_ROLES.");
                    return In(member, [.. list], literal: true, negate: op == "$nin", p);
                case "$null":
                    if (operand?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid($"{path}.$null", "Expected true or false.");
                    bool isNull = operand.GetValueKind() == JsonValueKind.True;
                    if (member is null) return True;
                    if (!CanBeNull(member.Type)) return Expression.Constant(!isNull);
                    var nullValue = Expression.Constant(null, member.Type);
                    return isNull ? Expression.Equal(member, nullValue) : Expression.NotEqual(member, nullValue);
                default:
                    throw Invalid(path, "Unknown operator.");
            }
        }

        private Expression Compare(MemberExpression? member, string op, JsonNode? operand, string path)
        {
            object? value = Scalar(operand, member?.Type, literal: true, $"{path}.{op}");
            if (member is null) return True;
            var right = Parameter(value, member.Type);
            return op switch
            {
                "$eq" => Expression.Equal(member, right),
                "$ne" => Expression.NotEqual(member, right),
                "$gt" => Expression.GreaterThan(member, right),
                "$gte" => Expression.GreaterThanOrEqual(member, right),
                "$lt" => Expression.LessThan(member, right),
                _ => Expression.LessThanOrEqual(member, right),
            };
        }

        // literal = false for values that came from a variable: they are data, never variables themselves.
        private Expression In(MemberExpression? member, List<JsonNode?> values, bool literal, bool negate, string path)
        {
            if (member is null)
            {
                for (int i = 0; i < values.Count; i++) Scalar(values[i], null, literal, $"{path}.{i}");
                return True;
            }
            var array = Array.CreateInstance(member.Type, values.Count);
            for (int i = 0; i < values.Count; i++) array.SetValue(Scalar(values[i], member.Type, literal, $"{path}.{i}"), i);
            Expression contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [member.Type],
                Parameter(array, array.GetType()), member);
            return negate ? Expression.Not(contains) : contains;
        }

        private List<JsonNode?> RoleValues() =>
            (variables?.Roles ?? []).Select(r => (JsonNode?)JsonValue.Create(r)).ToList();

        /// <summary>One value converted to <paramref name="type"/> (null at save time, when there is no type).</summary>
        private object? Scalar(JsonNode? operand, Type? type, bool literal, string path)
        {
            if (operand is not JsonValue value) throw Invalid(path, "Expected a single value (string, number or boolean).");
            if (literal && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().StartsWith('$'))
            {
                if (value.GetValue<string>() != CurrentUser)
                    throw Invalid(path, "Unknown variable; use $CURRENT_USER, or $CURRENT_ROLES alone with $eq, $ne, $in or $nin.");
                // Guid "D" format: what the audit interceptor writes into CreatedBy/LastModifiedBy strings.
                value = JsonValue.Create((variables?.UserId ?? Guid.Empty).ToString("D"));
            }
            return type is null ? null : Convert(value, type, path);
        }

        private static object Convert(JsonValue value, Type type, string path)
        {
            object? result;
            try
            {
                result = value.Deserialize(type, ValueOptions);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException or OverflowException)
            {
                throw Invalid(path, "The value does not match the property's type.");
            }
            return result switch
            {
                null => throw Invalid(path, "The value does not match the property's type."),
                // Npgsql only takes UTC for timestamptz parameters.
                DateTime { Kind: not DateTimeKind.Utc } => throw Invalid(path, "Dates must be in UTC (ending in Z)."),
                DateTimeOffset offset => offset.ToUniversalTime(),
                _ => result,
            };
        }

        /// <summary>A captured value, so EF Core sends it as a SQL parameter and reuses the query plan.</summary>
        private static MemberExpression Parameter(object? value, Type type)
        {
            object box = Activator.CreateInstance(typeof(StrongBox<>).MakeGenericType(type), [value])!;
            return Expression.Field(Expression.Constant(box), nameof(StrongBox<object>.Value));
        }

        private static bool IsRolesVariable(JsonNode? node) =>
            node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == CurrentRoles;
    }

    private static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static bool IsNumber(Type u) =>
        u == typeof(decimal) || (u.IsPrimitive && u != typeof(bool) && u != typeof(char) && u != typeof(nint) && u != typeof(nuint));

    private static bool IsOrderable(Type type)
    {
        var u = Underlying(type);
        return IsNumber(u) || u == typeof(DateTime) || u == typeof(DateTimeOffset);
    }

    private static bool IsScalar(Type type)
    {
        var u = Underlying(type);
        return u == typeof(string) || u == typeof(Guid) || u == typeof(bool) || u.IsEnum || IsOrderable(u);
    }
}

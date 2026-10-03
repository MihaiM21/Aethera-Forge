using System.Linq.Expressions;
using System.Reflection;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources;

/// <summary>
/// The sortable columns of one list endpoint (an allowlist of indexed, non-null properties) and the keyset paging over them. Build one per
/// resource as a static field:
/// <code>
/// static readonly SortDefinition&lt;Project&gt; Sorts = new SortDefinition&lt;Project&gt;("-createdAt")
///     .Add("createdAt", p =&gt; p.CreatedAt).Add("name", p =&gt; p.Name);
/// </code>
/// Supported column types: string, DateTimeOffset, int, long, bool. <c>id</c> is always the final tiebreaker.
/// </summary>
public sealed class SortDefinition<T> where T : Entity
{
    private readonly List<ISortColumn> _columns = [];

    public SortDefinition(string defaultSort) => DefaultSort = defaultSort;

    public string DefaultSort { get; }

    public IReadOnlyCollection<string> Allowed => _columns.Select(c => c.Name).ToList();

    public SortDefinition<T> Add<TKey>(string name, Expression<Func<T, TKey>> selector)
    {
        _columns.Add(new SortColumn<TKey>(name, selector));
        return this;
    }

    /// <summary>
    /// Runs the keyset query. <paramref name="filterContext"/> must describe every filter of the request (it ties a cursor to them).
    /// Returns up to <c>limit</c> entities and the cursor of the next page, or null.
    /// </summary>
    public async Task<(IReadOnlyList<T> Items, string? NextCursor)> PageAsync(
        IQueryable<T> query, string? sort, PageRequest page, KeysetCursor cursors, string filterContext, CancellationToken ct)
    {
        var request = page.Validated();
        var order = SortSpec.Parse(sort, Allowed, DefaultSort);
        var context = order.Canonical + "|" + filterContext;
        var columns = order.Fields.Select(f => (Column: _columns.First(c => c.Name == f.Name), f.Descending)).ToList();

        if (request.Cursor is not null)
        {
            var position = cursors.Decode(request.Cursor, context);
            if (position.SortValues.Count != columns.Count) throw new ApiProblemException(ApiProblems.InvalidCursor());
            query = query.Where(BuildKeyset(columns, position));
        }

        IOrderedQueryable<T>? ordered = null;
        foreach (var (column, descending) in columns) ordered = column.Apply(ordered ?? query, ordered is not null, descending);
        ordered = ordered is null ? query.OrderBy(e => e.Id) : ordered.ThenBy(e => e.Id);

        var fetched = await ordered.Take(request.Limit + 1).ToListAsync(ct);
        string? next = null;
        if (fetched.Count > request.Limit)
        {
            fetched.RemoveRange(request.Limit, fetched.Count - request.Limit);
            var last = fetched[^1];
            next = cursors.Encode(new KeysetPosition(columns.Select(c => c.Column.Format(last)).ToList(), last.Id), context);
        }

        return (fetched, next);
    }

    private Expression<Func<T, bool>> BuildKeyset(List<(ISortColumn Column, bool Descending)> columns, KeysetPosition position)
    {
        var p = Expression.Parameter(typeof(T), "e");
        var parsed = new List<(Expression Left, Expression Right, bool Descending, Func<Expression, Expression, Expression> Compare)>();
        for (var i = 0; i < columns.Count; i++)
        {
            var (column, descending) = columns[i];
            var (left, right) = column.Operands(p, position.SortValues[i]);
            parsed.Add((left, right, descending, column.Compare));
        }

        Expression? any = null;
        for (var i = 0; i <= parsed.Count; i++)
        {
            Expression? term = null;
            for (var j = 0; j < i; j++) term = And(term, Expression.Equal(parsed[j].Left, parsed[j].Right));

            Expression step;
            if (i < parsed.Count)
            {
                var (left, right, descending, compare) = parsed[i];
                var cmp = compare(left, right); // left - right as an int expression
                step = descending ? Expression.LessThan(cmp, Expression.Constant(0)) : Expression.GreaterThan(cmp, Expression.Constant(0));
            }
            else
            {
                var id = Expression.Property(p, nameof(Entity.Id));
                var idCmp = Expression.Call(id, typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!, Expression.Constant(position.Id));
                step = Expression.GreaterThan(idCmp, Expression.Constant(0));
            }

            term = And(term, step);
            any = any is null ? term : Expression.OrElse(any, term);
        }

        return Expression.Lambda<Func<T, bool>>(any!, p);

        static Expression And(Expression? left, Expression right) => left is null ? right : Expression.AndAlso(left, right);
    }

    private interface ISortColumn
    {
        string Name { get; }
        IOrderedQueryable<T> Apply(IQueryable<T> query, bool thenBy, bool descending);
        string? Format(T entity);
        (Expression Left, Expression Right) Operands(ParameterExpression parameter, string? raw);
        Expression Compare(Expression left, Expression right);
    }

    private sealed class SortColumn<TKey>(string name, Expression<Func<T, TKey>> selector) : ISortColumn
    {
        private readonly Func<T, TKey> _compiled = selector.Compile();

        public string Name { get; } = name;

        public IOrderedQueryable<T> Apply(IQueryable<T> query, bool thenBy, bool descending) => (thenBy, descending) switch
        {
            (false, false) => query.OrderBy(selector),
            (false, true) => query.OrderByDescending(selector),
            (true, false) => ((IOrderedQueryable<T>)query).ThenBy(selector),
            (true, true) => ((IOrderedQueryable<T>)query).ThenByDescending(selector),
        };

        public string? Format(T entity) => _compiled(entity) switch
        {
            null => null,
            DateTimeOffset d => KeysetCursor.Format(d),
            int i => KeysetCursor.Format(i),
            long l => KeysetCursor.Format(l),
            bool b => b ? "1" : "0",
            var other => other.ToString(),
        };

        public (Expression Left, Expression Right) Operands(ParameterExpression parameter, string? raw)
        {
            var value = Parse(raw);
            // A closure member (not a constant) so EF sends the value as a parameter.
            var holder = Expression.Constant(new Box<TKey>(value));
            var right = Expression.Property(holder, nameof(Box<TKey>.Value));
            var left = new ParameterReplacer(selector.Parameters[0], parameter).Visit(selector.Body);
            return (left, right);
        }

        public Expression Compare(Expression left, Expression right)
        {
            if (typeof(TKey) == typeof(string))
                return Expression.Call(StringCompare, left, right);
            if (typeof(TKey) == typeof(bool))
                return Expression.Subtract(Expression.Condition(left, Expression.Constant(1), Expression.Constant(0)),
                    Expression.Condition(right, Expression.Constant(1), Expression.Constant(0)));
            // CompareTo(...) is translated by EF Core to the plain comparison operators.
            var compareTo = typeof(TKey).GetMethod("CompareTo", [typeof(TKey)])
                ?? throw new InvalidOperationException($"Sort column '{Name}' has an unsupported type {typeof(TKey).Name}.");
            return Expression.Call(left, compareTo, right);
        }

        private static TKey Parse(string? raw)
        {
            if (raw is null) throw new ApiProblemException(ApiProblems.InvalidCursor());
            try
            {
                object value = typeof(TKey) switch
                {
                    var t when t == typeof(string) => raw,
                    var t when t == typeof(DateTimeOffset) => KeysetCursor.ParseDateTimeOffset(raw),
                    var t when t == typeof(int) => int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                    var t when t == typeof(long) => long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                    var t when t == typeof(bool) => raw == "1",
                    _ => throw new InvalidOperationException($"Unsupported sort column type {typeof(TKey).Name}."),
                };
                return (TKey)value;
            }
            catch (FormatException)
            {
                throw new ApiProblemException(ApiProblems.InvalidCursor());
            }
        }

        private static readonly MethodInfo StringCompare =
            typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    }

    private sealed class Box<TValue>(TValue value)
    {
        public TValue Value { get; } = value;
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

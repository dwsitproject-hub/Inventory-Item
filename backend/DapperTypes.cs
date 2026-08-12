using System.Data;
using Dapper;

namespace BcInventory.Api;

/// <summary>
/// Dapper has no built-in mapping for <see cref="DateOnly"/>/<see cref="TimeOnly"/> and throws
/// "The member … of type System.DateOnly cannot be used as a parameter value" the moment one is
/// passed. Npgsql maps both natively, so these handlers only need to hand the value over —
/// registering them once keeps every date filter working instead of forcing each call site to
/// remember a .ToDateTime() conversion.
/// </summary>
public static class DapperTypes
{
    public static void Register()
    {
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter p, DateOnly value) => p.Value = value;
        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s),
            _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to DateOnly")
        };
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter p, DateOnly? value) => p.Value = (object?)value ?? DBNull.Value;
        public override DateOnly? Parse(object value) => value switch
        {
            null or DBNull => null,
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType().Name} to DateOnly?")
        };
    }

    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override void SetValue(IDbDataParameter p, TimeOnly value) => p.Value = value;
        public override TimeOnly Parse(object value) => value switch
        {
            TimeOnly t => t,
            TimeSpan ts => TimeOnly.FromTimeSpan(ts),
            DateTime dt => TimeOnly.FromDateTime(dt),
            string s => TimeOnly.Parse(s),
            _ => throw new InvalidCastException($"Cannot convert {value?.GetType().Name ?? "null"} to TimeOnly")
        };
    }
}

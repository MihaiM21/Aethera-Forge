using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aethera.Infrastructure.Persistence;

/// <summary>Writes every <see cref="DateTimeOffset"/> as UTC (Npgsql rejects other offsets) and reads it back as UTC.</summary>
public sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v.ToUniversalTime());

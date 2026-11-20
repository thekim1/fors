using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fors.Infrastructure.Persistence;

public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());

namespace Fors.AdminApi.Common;

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total);

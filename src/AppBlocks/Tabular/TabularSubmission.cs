using System.Text.Json;

namespace AppBlocks.Tabular;

/// <summary>One change the runtime sends: <c>create</c>, <c>update</c> or <c>delete</c>, the row's key and checksum for the latter two, and the values.</summary>
public sealed record TabularChange(string Operation, string? Key, string? Checksum, JsonElement? Values);

/// <summary>A batch of changes to one record's rows, under one submission key so a retry is harmless.</summary>
public sealed record TabularSubmission(string? SubmissionKey, IReadOnlyList<TabularChange>? Changes);

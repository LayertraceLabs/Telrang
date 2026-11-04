namespace Telrang.Core.Ballots;

public sealed record RankedBallot(IReadOnlyList<string> Ranked, IReadOnlyList<string> Banned);

public sealed record PointsBallot(IReadOnlyDictionary<string, int> Points);

public enum Availability { No, Maybe, Yes }

/// <summary>Answers per option key; keys not present read as <see cref="Availability.No"/>.</summary>
public sealed record DatesBallot(IReadOnlyDictionary<string, Availability> Answers);

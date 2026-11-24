namespace Telrang.Core.Polls;

/// <summary>
/// The question types (DESIGN.md §18, §34.3). The numeric values are stored with nothing but the name, but new members only
/// ever go at the end.
/// </summary>
public enum PollType { Ranked, Points, Dates, Choice, Text, Number }

/// <summary>How a <see cref="PollType.Choice"/> question shows its options; a dropdown needs a maximum of one selection.</summary>
public enum ChoiceDisplay { Buttons, Dropdown }
public enum ScoringScheme { Borda, Dowdall }
/// <summary>
/// Who sees the results besides the owner (DESIGN.md §25): <see cref="Creator"/> nobody, <see cref="AfterVote"/> voters
/// (a visitor whose identity has a ballot), <see cref="Open"/> anyone with the link (public polls only).
/// </summary>
public enum ResultsVisibility { Creator, AfterVote, Open }

/// <summary>When the results show to those <see cref="ResultsVisibility"/> lets in (DESIGN.md §25).</summary>
public enum ResultsTiming { WhileOpen, AfterClose }

/// <summary>
/// Who may respond. <see cref="Public"/>: anyone with the link. <see cref="Accounts"/>: signed-in users with a
/// verified email. <see cref="Invite"/>: personal invite links.
/// </summary>
public enum VoterMode { Accounts, Public, Invite }

public enum NamesMode { None, Optional, Required }

/// <summary>How many responses one voter identity may submit.</summary>
public enum ResponsePolicy { One, Many }

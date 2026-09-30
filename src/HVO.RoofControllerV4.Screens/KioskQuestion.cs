namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// A question the kiosk asks before it sends something that needs confirming: a title, what it is about, and the answers
/// that send it. Cancel is always offered as well.
/// </summary>
public sealed record KioskQuestion(string Title, IReadOnlyList<string> Lines, IReadOnlyList<KioskAnswer> Answers);

/// <summary>One answer to a <see cref="KioskQuestion"/>: its button's label and what it does.</summary>
public sealed record KioskAnswer(string Label, Func<Task> Act);

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>What the settings and system pages share: their message, a question, and a two-line button.</summary>
internal static class KioskPageParts
{
    /// <summary>The page's busy line and its last message, or nothing.</summary>
    public static IEnumerable<Control> Status(string? busy, KioskNotice? message, KioskMetrics metrics)
    {
        if (busy is not null)
        {
            var line = KioskTheme.Label(busy, metrics.Font, KioskTheme.Muted);
            line.Name = "busy";
            yield return line;
        }

        if (message is not null)
        {
            yield return KioskTheme.Banner(message.Text, message.Level, metrics, "message");
        }
    }

    /// <summary>A question: its title, what it is about, its answers, and Cancel.</summary>
    public static Control Question(KioskQuestion question, KioskMetrics metrics, Action cancel)
    {
        var panel = new StackPanel { Name = "question", Spacing = metrics.Gap * 1.5 };
        panel.Children.Add(KioskTheme.Label(question.Title, metrics.Large, weight: FontWeight.SemiBold));
        foreach (var line in question.Lines)
        {
            panel.Children.Add(KioskTheme.Label(line, metrics.Font));
        }

        var answers = new WrapPanel { ItemSpacing = metrics.Gap * 2, LineSpacing = metrics.Gap };
        for (var i = 0; i < question.Answers.Count; i++)
        {
            var answer = question.Answers[i];
            var button = KioskTheme.TouchButton(answer.Label, $"answer-{i}", metrics, () => _ = answer.Act());
            KioskTheme.Colour(button, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
            answers.Children.Add(button);
        }

        answers.Children.Add(KioskTheme.TouchButton("Cancel", "question-cancel", metrics, cancel));
        panel.Children.Add(answers);
        return KioskTheme.Card(panel, metrics);
    }

    /// <summary>A full-width button with a heading and a second, quieter line.</summary>
    public static Button TwoLineButton(string heading, string? detail, string name, KioskMetrics metrics, Action click)
    {
        var button = KioskTheme.TouchButton(heading, name, metrics, click);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        var content = new StackPanel { Spacing = metrics.Gap / 2 };
        content.Children.Add(new TextBlock { Text = heading, FontSize = metrics.Font, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
        {
            content.Children.Add(new TextBlock { Text = detail, FontSize = metrics.Small, Foreground = KioskTheme.Muted, TextWrapping = TextWrapping.Wrap });
        }

        button.Content = content;
        return button;
    }

    /// <summary>A row of buttons that wraps on a narrow screen.</summary>
    public static WrapPanel Row(KioskMetrics metrics, params Control[] controls)
    {
        var row = new WrapPanel { ItemSpacing = metrics.Gap * 2, LineSpacing = metrics.Gap };
        foreach (var control in controls)
        {
            row.Children.Add(control);
        }

        return row;
    }
}

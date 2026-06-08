using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SDRIQStreamer.App;

public partial class SetupWizardWindow : Window
{
    private const string EmbeddedGuideResourceName = "SDRIQStreamer.App.SETUP_GUIDE_WIZARD.md";
    private static readonly string ResourceAssemblyName = typeof(SetupWizardWindow).Assembly.GetName().Name ?? "SDRIQStreamer";
    private readonly IReadOnlyList<WizardPage> _pages;
    private int _currentIndex;

    private readonly TextBlock _wizardTitleText;
    private readonly TextBlock _wizardStepText;
    private readonly TextBlock _wizardHintText;
    private readonly StackPanel _contentPanel;
    private readonly Button _backButton;
    private readonly Button _nextButton;

    public SetupWizardWindow()
    {
        InitializeComponent();

        _wizardTitleText = RequireControl<TextBlock>("WizardTitleText");
        _wizardStepText = RequireControl<TextBlock>("WizardStepText");
        _wizardHintText = RequireControl<TextBlock>("WizardHintText");
        _contentPanel = RequireControl<StackPanel>("ContentPanel");
        _backButton = RequireControl<Button>("BackButton");
        _nextButton = RequireControl<Button>("NextButton");

        _backButton.Click += OnBackClicked;
        _nextButton.Click += OnNextClicked;

        var markdown = TryLoadEmbeddedGuideMarkdown();
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            _pages = ParseWizardPages(markdown);
            _wizardHintText.Text = "Loaded from bundled setup guide.";
        }
        else
        {
            _pages = [new WizardPage("Setup guide not found", "Embedded setup guide resource is missing.")];
            _wizardHintText.Text = "Bundled guide is missing.";
        }

        RenderCurrentPage();
    }

    private T RequireControl<T>(string name) where T : Control
    {
        var control = this.FindControl<T>(name);
        if (control is null)
            throw new InvalidOperationException($"SetupWizardWindow is missing required control: {name}");
        return control;
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentIndex <= 0)
            return;

        _currentIndex--;
        RenderCurrentPage();
    }

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentIndex >= _pages.Count - 1)
        {
            Close();
            return;
        }

        _currentIndex++;
        RenderCurrentPage();
    }

    private void RenderCurrentPage()
    {
        var page = _pages[_currentIndex];
        _wizardTitleText.Text = page.Title;
        _wizardStepText.Text = $"Step {_currentIndex + 1} of {_pages.Count}";
        _backButton.IsEnabled = _currentIndex > 0;
        _nextButton.IsEnabled = true;
        _nextButton.Content = _currentIndex < _pages.Count - 1 ? "Next" : "Done";

        _contentPanel.Children.Clear();
        BuildFormattedContent(page.Body, _contentPanel);
    }

    private static string TryLoadEmbeddedGuideMarkdown()
    {
        var assembly = typeof(SetupWizardWindow).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedGuideResourceName);
        if (stream is null)
            return string.Empty;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<WizardPage> ParseWizardPages(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var pages = new List<WizardPage>();
        var currentTitle = string.Empty;
        var bodyBuilder = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // Bug fix 2026-06-08: the guide's whole preamble (the "# " title and
            // the intro paragraph before the first "## " step) was dropped, so
            // it never appeared in the Setup Guide. Root cause: pages only opened
            // on "## ", leaving the preamble with no title, which the adds below
            // skip. Fix: open the intro page on the top-level "# " heading so the
            // preamble is captured as the first page. ("## " fails this prefix.)
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                currentTitle = line[2..].Trim();
                bodyBuilder.Clear();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(currentTitle))
                    pages.Add(new WizardPage(currentTitle, bodyBuilder.ToString().Trim()));

                currentTitle = line[3..].Trim();
                bodyBuilder.Clear();
                continue;
            }

            if (!string.IsNullOrWhiteSpace(currentTitle))
                bodyBuilder.AppendLine(rawLine);
        }

        if (!string.IsNullOrWhiteSpace(currentTitle))
            pages.Add(new WizardPage(currentTitle, bodyBuilder.ToString().Trim()));

        if (pages.Count > 0)
            return pages;

        return [new WizardPage("Setup Guide", markdown)];
    }

    private static void BuildFormattedContent(string content, Panel target)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var inCodeBlock = false;
        var codeBuilder = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (line.Trim() == "```")
            {
                if (inCodeBlock)
                {
                    AddCodeBlock(target, codeBuilder.ToString().TrimEnd());
                    codeBuilder.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBuilder.AppendLine(rawLine);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                target.Children.Add(new Border { Height = 4 });
                continue;
            }

            if (line == "---")
            {
                target.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
                continue;
            }

            if (TryAddImage(target, line))
                continue;

            if (TryAddExternalLink(target, line))
                continue;

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                target.Children.Add(new TextBlock
                {
                    Text = line[4..].Trim(),
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 8, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                target.Children.Add(new TextBlock
                {
                    Text = $"• {line[2..].Trim()}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 0, 0, 0)
                });
                continue;
            }

            if (Regex.IsMatch(line, @"^\d+\.\s+"))
            {
                target.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 0, 0, 0)
                });
                continue;
            }

            target.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (inCodeBlock && codeBuilder.Length > 0)
            AddCodeBlock(target, codeBuilder.ToString().TrimEnd());
    }

    private static bool TryAddExternalLink(Panel target, string line)
    {
        var match = Regex.Match(line, @"^\s*(?:-\s+)?\[(?<label>[^\]]+)\]\((?<url>https?://[^)]+)\)$");
        if (!match.Success)
            return false;

        var label = match.Groups["label"].Value.Trim();
        var url = match.Groups["url"].Value.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var button = new Button
        {
            Content = string.IsNullOrWhiteSpace(label) ? url : label,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = Brushes.DodgerBlue,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 2),
            FontSize = 12
        };

        button.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore launch failure; keep wizard flow uninterrupted.
            }
        };

        target.Children.Add(button);
        return true;
    }

    private static void AddCodeBlock(Panel target, string code)
    {
        target.Children.Add(new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 4),
            Background = new SolidColorBrush(Color.Parse("#F7F7F7")),
            Child = new TextBlock
            {
                Text = code,
                FontFamily = FontFamily.Parse("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            }
        });
    }

    private static bool TryAddImage(Panel target, string line)
    {
        var match = Regex.Match(line, @"^!\[(?<alt>[^\]]*)\]\((?<path>[^)]+)\)$");
        if (!match.Success)
            return false;

        var alt = match.Groups["alt"].Value.Trim();
        var imageReference = match.Groups["path"].Value.Trim();
        if (!TryOpenImageStream(imageReference, out var imageStream))
        {
            target.Children.Add(new TextBlock
            {
                Text = $"[Image not found] {imageReference}",
                Foreground = Brushes.DarkOrange,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            return true;
        }

        try
        {
            using (imageStream)
            {
                var bitmap = new Bitmap(imageStream);
                target.Children.Add(new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 420,
                    Margin = new Thickness(0, 6, 0, 4)
                });
            }

            if (!string.IsNullOrWhiteSpace(alt))
            {
                target.Children.Add(new TextBlock
                {
                    Text = alt,
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        catch
        {
            target.Children.Add(new TextBlock
            {
                Text = $"[Image load failed] {imageReference}",
                Foreground = Brushes.DarkOrange,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return true;
    }

    private static bool TryOpenImageStream(string reference, out Stream stream)
    {
        stream = Stream.Null;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var normalized = reference.Trim().Replace('\\', '/');

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme.Equals("avares", StringComparison.OrdinalIgnoreCase) &&
                AssetLoader.Exists(absoluteUri))
            {
                stream = AssetLoader.Open(absoluteUri);
                return true;
            }

            if (absoluteUri.IsFile && File.Exists(absoluteUri.LocalPath))
            {
                stream = File.OpenRead(absoluteUri.LocalPath);
                return true;
            }
        }

        if (Path.IsPathRooted(reference) && File.Exists(reference))
        {
            stream = File.OpenRead(reference);
            return true;
        }

        var appResourceUri = new Uri($"avares://{ResourceAssemblyName}/{normalized.TrimStart('/')}");
        if (!AssetLoader.Exists(appResourceUri))
            return false;

        stream = AssetLoader.Open(appResourceUri);
        return true;
    }

    private sealed record WizardPage(string Title, string Body);
}

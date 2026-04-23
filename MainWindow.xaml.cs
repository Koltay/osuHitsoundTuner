using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using Microsoft.Win32;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OsuHitsoundTuner;

public partial class MainWindow : Window
{
    private const float SilenceThreshold = 0.01f;
    private const float NearSilentPeakThreshold = 0.0035f;
    private const double StartAudioWindowSeconds = 0.005;
    private const double ShortClipThresholdSeconds = 1.0;
    private const double ShortClipLeadInSeconds = 0.15;

    private static readonly string[] SupportedExtensions = [".wav", ".ogg", ".mp3"];

    private static readonly string[] StandardHitsounds =
    [
        "normal-hitnormal",
        "normal-hitwhistle",
        "normal-hitfinish",
        "normal-hitclap",
        "normal-slidertick",
        "normal-sliderslide",
        "normal-sliderwhistle",
        "drum-hitnormal",
        "drum-hitwhistle",
        "drum-hitfinish",
        "drum-hitclap",
        "drum-slidertick",
        "drum-sliderslide",
        "drum-sliderwhistle",
        "soft-hitnormal",
        "soft-hitwhistle",
        "soft-hitfinish",
        "soft-hitclap",
        "soft-slidertick",
        "soft-sliderslide",
        "soft-sliderwhistle"
    ];

    private readonly List<SkinItem> _skins = [];
    private readonly Dictionary<string, HitsoundOption> _availableHitsoundFiles = new(StringComparer.OrdinalIgnoreCase);
    private AudioBuffer? _loadedAudio;
    private string? _selectedFilePath;
    private WaveOutEvent? _playbackOutput;
    private WaveStream? _playbackStream;
    private readonly List<string> _playbackTempPaths = [];
    private bool _isDraggingSelection;
    private double _dragStartX;

    public MainWindow()
    {
        InitializeComponent();
        AutoDetectSkinFolder();
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(SkinRootTextBox.Text) && Directory.Exists(SkinRootTextBox.Text))
        {
            dialog.InitialDirectory = SkinRootTextBox.Text;
        }

        if (dialog.ShowDialog() == true)
        {
            SkinRootTextBox.Text = dialog.FolderName;
            LoadSkinsFromRoot();
        }
    }

    private void OpenSkinFolder_Click(object sender, RoutedEventArgs e)
    {
        var skin = SkinComboBox.SelectedItem as SkinItem;
        if (skin is null || !Directory.Exists(skin.Path))
        {
            SetStatus("No valid skin folder is selected.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = skin.Path,
            UseShellExecute = true
        });
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        var skin = SkinComboBox.SelectedItem as SkinItem;
        if (skin is null)
        {
            SetStatus("Select a skin first to check again.");
            return;
        }

        PopulateAvailableHitsoundsForSelectedSkin(showActionPrompt: false, preserveSelection: true);
    }

    private void SkinComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkinComboBox.SelectedItem is null)
        {
            return;
        }

        PopulateAvailableHitsoundsForSelectedSkin(showActionPrompt: false, preserveSelection: false);
    }

    private void HitsoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadSelectedHitsound();
    }

    private void PrevHitsound_Click(object sender, RoutedEventArgs e)
    {
        if (HitsoundComboBox.Items.Count == 0 || HitsoundComboBox.SelectedIndex <= 0)
        {
            return;
        }

        HitsoundComboBox.SelectedIndex -= 1;
    }

    private void NextHitsound_Click(object sender, RoutedEventArgs e)
    {
        if (HitsoundComboBox.Items.Count == 0 || HitsoundComboBox.SelectedIndex >= HitsoundComboBox.Items.Count - 1)
        {
            return;
        }

        HitsoundComboBox.SelectedIndex += 1;
    }

    private void PlayOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAudioLoaded())
        {
            return;
        }

        if (IsAudioEmpty(_loadedAudio!))
        {
            SetStatus("This file is empty audio. Playback is not required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedFilePath) || !File.Exists(_selectedFilePath))
        {
            SetStatus("Selected file path is missing. Reload a hitsound and try again.");
            return;
        }

        if (TryPlayAudioFromPath(_selectedFilePath, "Playing current hitsound."))
        {
            SetStatus("Playing current hitsound.");
        }
    }
    
    private void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAudioLoaded())
        {
            return;
        }

        if (IsAudioEmpty(_loadedAudio!))
        {
            SetStatus("This file is empty audio. Selected playback is not required.");
            return;
        }

        var selected = TrimToSliderRange(_loadedAudio!);
        if (selected.Samples.Length == 0)
        {
            SetStatus("Selected range is empty.");
            return;
        }

        if (TryPlayAudio(selected, "Playing selected range."))
        {
            SetStatus("Playing selected range.");
        }
    }

    private void PreviewTrimmed_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAudioLoaded())
        {
            return;
        }

        if (IsAudioEmpty(_loadedAudio!))
        {
            SetStatus("This file is empty audio. Preview is not required.");
            SetTrimAnalysisWarning("Empty audio detected. No trimming is required.");
            return;
        }

        var trimmed = TrimToSliderRange(_loadedAudio!);
        if (trimmed.Samples.Length == 0)
        {
            SetStatus("Preview range produced empty audio.");
            return;
        }

        if (TryPlayAudio(trimmed, "Playing changed preview."))
        {
            SetStatus("Playing changed preview.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        base.OnClosed(e);
    }

    private void TrimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadedAudio is null)
        {
            return;
        }

        if (StartSlider.Value > EndSlider.Value)
        {
            EndSlider.Value = StartSlider.Value;
        }

        DrawWaveform();
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawWaveform();
    }

    private void WaveformCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_loadedAudio is null || _loadedAudio.DurationSeconds <= 0)
        {
            return;
        }

        _isDraggingSelection = true;
        _dragStartX = e.GetPosition(WaveformCanvas).X;
        WaveformCanvas.CaptureMouse();
        UpdateDragSelection(_dragStartX, _dragStartX);
    }

    private void WaveformCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSelection)
        {
            return;
        }

        var currentX = e.GetPosition(WaveformCanvas).X;
        UpdateDragSelection(_dragStartX, currentX);
    }

    private void WaveformCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingSelection)
        {
            return;
        }

        _isDraggingSelection = false;
        WaveformCanvas.ReleaseMouseCapture();
        var endX = e.GetPosition(WaveformCanvas).X;
        UpdateDragSelection(_dragStartX, endX);
    }

    private void SaveTrimmed_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAudioLoaded())
        {
            return;
        }

        if (IsAudioEmpty(_loadedAudio!))
        {
            SetStatus("This file is empty audio. Trimming is not required.");
            SetTrimAnalysisWarning("Empty audio detected. There is nothing to trim at the start.");
            return;
        }

        var trimmed = TrimToSliderRange(_loadedAudio!);
        if (trimmed.Samples.Length == 0)
        {
            SetStatus("Trim range produced empty audio.");
            return;
        }

        var savePath = PickSavePath("trimmed");
        if (savePath is null)
        {
            return;
        }

        WriteWav(trimmed, savePath);
        SetStatus($"Trimmed WAV saved: {savePath}");
    }

    private void RemoveSilence_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAudioLoaded())
        {
            return;
        }

        if (IsAudioEmpty(_loadedAudio!))
        {
            SetStatus("This file is empty audio. Silence removal is not required.");
            SetTrimAnalysisWarning("Empty audio detected. There is nothing to trim at the start.");
            return;
        }

        var trimmed = TrimLeadingSilence(_loadedAudio!, SilenceThreshold);
        if (trimmed.Samples.Length == 0)
        {
            SetStatus("Could not find non-silent content.");
            return;
        }

        var savePath = PickSavePath("nosilence");
        if (savePath is null)
        {
            return;
        }

        WriteWav(trimmed, savePath);
        SetStatus($"Silence removed and WAV saved: {savePath}");
    }

    private void ConvertToWav_Click(object sender, RoutedEventArgs e)
    {
        var skin = SkinComboBox.SelectedItem as SkinItem;
        if (skin is null)
        {
            SetStatus("Select a skin before converting.");
            return;
        }

        if (_availableHitsoundFiles.Count == 0)
        {
            SetStatus("No available hitsounds to convert.");
            return;
        }

        var convertedCount = 0;
        var skippedCount = 0;

        foreach (var option in _availableHitsoundFiles.Values)
        {
            var ext = Path.GetExtension(option.FilePath).ToLowerInvariant();
            if (ext == ".wav")
            {
                skippedCount++;
                continue;
            }

            try
            {
                var audio = ReadAudio(option.FilePath);
                var wavPath = Path.Combine(Path.GetDirectoryName(option.FilePath)!, $"{option.Key}.wav");
                WriteWav(audio, wavPath);
                convertedCount++;
            }
            catch
            {
                skippedCount++;
            }
        }

        SetStatus($"Convert all done. Converted {convertedCount}, skipped {skippedCount}.");
    }

    private void AutoDetectSkinFolder()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!", "Skins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "osu!", "Skins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "osu!", "Skins")
        };

        var found = candidates.FirstOrDefault(Directory.Exists);
        if (found is not null)
        {
            SkinRootTextBox.Text = found;
            SetStatus($"Auto-detected skins folder: {found}");
            LoadSkinsFromRoot();
            return;
        }

        SetStatus("Could not auto-detect osu! skins folder. Use Browse.");
    }

    private void LoadSkinsFromRoot()
    {
        _skins.Clear();
        _availableHitsoundFiles.Clear();
        SkinComboBox.ItemsSource = null;
        SkinComboBox.SelectedIndex = -1;
        HitsoundComboBox.ItemsSource = null;
        HitsoundComboBox.SelectedIndex = -1;
        ReplaceEmptyAudioButton.IsEnabled = false;
        TrimAnalysisTextBlock.Text = "Load a hitsound to analyze trim readiness.";
        PerfectListBox.ItemsSource = null;
        RecommendedListBox.ItemsSource = null;
        WarningListBox.ItemsSource = null;
        UpdateAnalysisNotification(new SkinAnalysisResult([], [], []));

        var root = SkinRootTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            SetStatus("Skin folder path does not exist.");
            return;
        }

        var skins = Directory
            .GetDirectories(root)
            .Select(CreateSkinItem)
            .OrderBy(item => item.FolderName)
            .ToList();

        _skins.AddRange(skins);
        SkinComboBox.ItemsSource = _skins;
        if (_skins.Count > 0)
        {
            SetStatus($"Detected {_skins.Count} skins. Select one to load hitsounds.");
        }
        else
        {
            SetStatus("No skin folders found.");
        }
    }

    private void LoadSelectedHitsound()
    {
        _loadedAudio = null;
        _selectedFilePath = null;
        ReplaceEmptyAudioButton.IsEnabled = false;
        WaveformCanvas.Children.Clear();
        SetTrimAnalysisNeutral("Load a hitsound to analyze trim readiness.");

        var hitsound = HitsoundComboBox.SelectedItem as HitsoundOption;
        if (hitsound is null)
        {
            SetStatus("Select an available hitsound.");
            return;
        }

        try
        {
            _selectedFilePath = hitsound.FilePath;
            _loadedAudio = ReadAudio(hitsound.FilePath);

            var duration = _loadedAudio.DurationSeconds;
            StartSlider.Minimum = 0;
            StartSlider.Maximum = duration;
            EndSlider.Minimum = 0;
            EndSlider.Maximum = duration;
            StartSlider.Value = 0;
            EndSlider.Value = duration;

            DrawWaveform();
            if (IsAudioEmpty(_loadedAudio))
            {
                SetStatus($"Loaded empty audio: {Path.GetFileName(hitsound.FilePath)}. No trim needed.");
                SetTrimAnalysisWarning("Empty audio detected. No trimming is required.");
            }
            else
            {
                SetStatus($"Loaded: {Path.GetFileName(hitsound.FilePath)} ({duration:0.000}s)");
                UpdateTrimAnalysis(_loadedAudio);

                if (IsNearSilentAudio(_loadedAudio))
                {
                    SetTrimAnalysisWarning("Near-silent audio detected. Consider replacing this file with an empty audio file.");
                    ReplaceEmptyAudioButton.IsEnabled = true;
                }
                else
                {
                    ReplaceEmptyAudioButton.IsEnabled = false;
                }
            }
        }
        catch (Exception ex)
        {
            _loadedAudio = null;
            _selectedFilePath = null;
            ReplaceEmptyAudioButton.IsEnabled = false;
            SetStatus($"Failed to read audio: {ex.Message}");
            SetTrimAnalysisWarning("Unable to analyze the selected audio file.");
        }
    }

    private void PopulateAvailableHitsoundsForSelectedSkin(bool showActionPrompt, bool preserveSelection)
    {
        var previousSelectionKey = preserveSelection
            ? (HitsoundComboBox.SelectedItem as HitsoundOption)?.Key
            : null;

        _availableHitsoundFiles.Clear();
        HitsoundComboBox.ItemsSource = null;
        HitsoundComboBox.SelectedIndex = -1;

        var skin = SkinComboBox.SelectedItem as SkinItem;
        if (skin is null)
        {
            SetStatus("Select a skin.");
            return;
        }

        foreach (var hitsoundName in StandardHitsounds)
        {
            var filePath = FindHitsoundFile(skin.Path, hitsoundName);
            if (filePath is not null && TryCreateHitsoundOption(hitsoundName, filePath, out var option))
            {
                _availableHitsoundFiles[hitsoundName] = option;
            }
        }

        var availableHitsounds = StandardHitsounds
            .Where(name => _availableHitsoundFiles.ContainsKey(name))
            .Select(name => _availableHitsoundFiles[name])
            .ToList();

        HitsoundComboBox.ItemsSource = availableHitsounds;

        if (availableHitsounds.Count == 0)
        {
            SetStatus($"Skin '{skin.FolderName}' has no standard hitsound files.");
            SetTrimAnalysisNeutral("No available hitsounds to analyze in this skin.");
            UpdateAnalysisTab(skin, new SkinAnalysisResult([], [], []));
            return;
        }

        if (!string.IsNullOrWhiteSpace(previousSelectionKey))
        {
            var preserved = availableHitsounds.FirstOrDefault(option => option.Key == previousSelectionKey);
            if (preserved is not null)
            {
                HitsoundComboBox.SelectedItem = preserved;
            }
        }

        if (HitsoundComboBox.SelectedItem is null)
        {
            HitsoundComboBox.SelectedIndex = 0;
        }

        SetStatus($"Skin '{skin.FolderName}' has {availableHitsounds.Count} available hitsounds.");

        var analysis = AnalyzeSkinStatuses(availableHitsounds);
        UpdateAnalysisTab(skin, analysis);

        if (showActionPrompt)
        {
            PromptActionsForSkin(skin, availableHitsounds, analysis);
        }
    }

    private void PromptActionsForSkin(SkinItem skin, IReadOnlyList<HitsoundOption> availableHitsounds, SkinAnalysisResult analysis)
    {
        var suggestions = analysis.GetSuggestions();
        if (suggestions.Count == 0)
        {
            return;
        }

        var actionable = suggestions
            .Where(item => item.IsActionable)
            .ToList();

        var perfectLines = analysis.PerfectItems.Select(item => $"- {item.Label}").ToList();
        var recommendedLines = analysis.RecommendedItems.Select(item => $"- {item.Label}").ToList();
        var warningLines = analysis.WarningItems.Select(item => $"- {item.Label}").ToList();

        var messageSections = new List<string>
        {
            $"Skin: {skin.DisplayLabel}",
            string.Empty,
            "Perfect (No changes required):",
            perfectLines.Count > 0 ? string.Join("\n", perfectLines) : "- None",
            string.Empty,
            "Recommended (WAV files):",
            recommendedLines.Count > 0 ? string.Join("\n", recommendedLines) : "- None",
            string.Empty,
            "Warning (Needs trimming):",
            warningLines.Count > 0 ? string.Join("\n", warningLines) : "- None",
            string.Empty,
            "Open the first actionable hitsound now?"
        };

        var message = string.Join("\n", messageSections);

        var result = MessageBox.Show(
            message,
            "Skin Analysis",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes && actionable.Count > 0)
        {
            var firstTarget = actionable[0].Hitsound;
            HitsoundComboBox.SelectedItem = availableHitsounds.FirstOrDefault(option => option.Key == firstTarget);
        }
    }

    private SkinAnalysisResult AnalyzeSkinStatuses(IReadOnlyList<HitsoundOption> availableHitsounds)
    {
        var perfectItems = new List<AnalysisListItem>();
        var recommendedItems = new List<AnalysisListItem>();
        var warningItems = new List<AnalysisListItem>();

        foreach (var hitsound in availableHitsounds)
        {
            try
            {
                var ext = Path.GetExtension(hitsound.FilePath).ToLowerInvariant();
                var audio = ReadAudio(hitsound.FilePath);

                if (IsAudioEmpty(audio))
                {
                    perfectItems.Add(new AnalysisListItem(hitsound.Key, hitsound.DisplayName, "Empty audio (intended silence)."));
                    continue;
                }

                if (IsNearSilentAudio(audio))
                {
                    warningItems.Add(new AnalysisListItem(hitsound.Key, hitsound.DisplayName, "Near-silent audio; consider replacing with empty audio."));
                }

                if (ext is ".ogg" or ".mp3")
                {
                    recommendedItems.Add(new AnalysisListItem(hitsound.Key, hitsound.DisplayName, $"Convert {ext} to WAV."));
                }

                if (!HasAudioAtStart(audio))
                {
                    warningItems.Add(new AnalysisListItem(hitsound.Key, hitsound.DisplayName, "Leading silence detected."));
                }

                if (!IsNearSilentAudio(audio) && ext == ".wav" && HasAudioAtStart(audio))
                {
                    perfectItems.Add(new AnalysisListItem(hitsound.Key, hitsound.DisplayName, "Ready to use."));
                }
            }
            catch
            {
                continue;
            }
        }

        return new SkinAnalysisResult(perfectItems, recommendedItems, warningItems);
    }

    private void UpdateAnalysisTab(SkinItem skin, SkinAnalysisResult analysis)
    {
        PerfectListBox.ItemsSource = analysis.PerfectItems;
        RecommendedListBox.ItemsSource = analysis.RecommendedItems;
        WarningListBox.ItemsSource = analysis.WarningItems;

        UpdateAnalysisNotification(analysis);
    }

    private void UpdateAnalysisNotification(SkinAnalysisResult analysis)
    {
        var actionCount = analysis.ActionableCount;
        if (actionCount > 0)
        {
            AnalysisBadge.Visibility = Visibility.Visible;
            AnalysisBadgeText.Text = actionCount.ToString();
            AnalysisActionPopup.Visibility = Visibility.Visible;

            var recommended = analysis.RecommendedItems.ToList();
            var warning = analysis.WarningItems.ToList();

            var lines = new List<string>();

            if (recommended.Count > 0)
            {
                lines.Add("Recommended:");
                lines.AddRange(recommended.Select(item => $"- {item.Label}"));
            }

            if (warning.Count > 0)
            {
                lines.Add("Warning:");
                lines.AddRange(warning.Select(item => $"- {item.Label}"));
            }

            AnalysisActionPopupText.Text = string.Join("\n", lines);
            return;
        }

        AnalysisBadge.Visibility = Visibility.Collapsed;
        AnalysisBadgeText.Text = "0";
        AnalysisActionPopup.Visibility = Visibility.Collapsed;
        AnalysisActionPopupText.Text = "Action required";
    }

    private void AnalysisIssue_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.SelectedItem is not AnalysisListItem issue)
        {
            return;
        }

        if (!_availableHitsoundFiles.TryGetValue(issue.Key, out var option))
        {
            return;
        }

        RootTabControl.SelectedIndex = 0;
        HitsoundComboBox.SelectedItem = option;
    }

    private static bool TryCreateHitsoundOption(string hitsoundName, string filePath, out HitsoundOption option)
    {
        option = null!;

        try
        {
            using var _ = OpenAudioForValidation(filePath);
            option = new HitsoundOption(hitsoundName, $"{hitsoundName}{Path.GetExtension(filePath).ToLowerInvariant()}", filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SkinItem CreateSkinItem(string path)
    {
        var folderName = Path.GetFileName(path);
        var (skinName, skinAuthor) = ReadSkinMetadata(path);
        var displayLabel = $"{folderName}[{skinName}]({skinAuthor})";
        return new SkinItem(path, folderName, skinName, skinAuthor, displayLabel);
    }

    private static (string SkinName, string SkinAuthor) ReadSkinMetadata(string skinFolderPath)
    {
        var iniPath = Path.Combine(skinFolderPath, "skin.ini");
        if (!File.Exists(iniPath))
        {
            return ("Unknown", "Unknown");
        }

        string? name = null;
        string? author = null;

        foreach (var rawLine in File.ReadLines(iniPath, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//") || line.StartsWith(";"))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex < 0)
            {
                separatorIndex = line.IndexOf('=');
            }

            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("Author", StringComparison.OrdinalIgnoreCase))
            {
                author = value;
            }
        }

        return (
            string.IsNullOrWhiteSpace(name) ? "Unknown" : name,
            string.IsNullOrWhiteSpace(author) ? "Unknown" : author);
    }

    private string? FindHitsoundFile(string skinFolder, string hitsoundBaseName)
    {
        foreach (var ext in SupportedExtensions)
        {
            var path = Path.Combine(skinFolder, hitsoundBaseName + ext);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static AudioBuffer ReadAudio(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".ogg" => ReadFromWaveStream(new VorbisWaveReader(path)),
            _ => ReadFromSampleProvider(new AudioFileReader(path))
        };
    }

    private static IDisposable OpenAudioForValidation(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".ogg" => new VorbisWaveReader(path),
            _ => new AudioFileReader(path)
        };
    }

    private static AudioBuffer ReadFromWaveStream(WaveStream stream)
    {
        using (stream)
        {
            return ReadFromSampleProvider(stream.ToSampleProvider(), stream.WaveFormat.SampleRate, stream.WaveFormat.Channels);
        }
    }

    private static AudioBuffer ReadFromSampleProvider(AudioFileReader reader)
    {
        using (reader)
        {
            return ReadFromSampleProvider(reader, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        }
    }

    private static AudioBuffer ReadFromSampleProvider(ISampleProvider provider, int sampleRate, int channels)
    {
        var samples = new List<float>(sampleRate * channels);
        var buffer = new float[4096];
        while (true)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            samples.AddRange(buffer.Take(read));
        }

        return new AudioBuffer(samples.ToArray(), sampleRate, channels);
    }

    private void DrawWaveform()
    {
        WaveformCanvas.Children.Clear();
        if (_loadedAudio is null)
        {
            return;
        }

        var width = Math.Max(1, WaveformCanvas.ActualWidth);
        var height = Math.Max(1, WaveformCanvas.ActualHeight);
        var points = BuildWavePoints(_loadedAudio, (int)width);
        if (points.Length == 0)
        {
            return;
        }

        var midY = height / 2.0;
        var amplitudeScale = (height / 2.2);
        for (var i = 0; i < points.Length; i++)
        {
            var x = (i / (double)Math.Max(1, points.Length - 1)) * width;
            var yTop = midY - (points[i] * amplitudeScale);
            var yBottom = midY + (points[i] * amplitudeScale);

            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = yTop,
                Y2 = yBottom,
                Stroke = new SolidColorBrush(Color.FromRgb(0x67, 0xE8, 0xF9)),
                StrokeThickness = 1
            };

            WaveformCanvas.Children.Add(line);
        }

        DrawTrimOverlay(width, height);
    }

    private void DrawTrimOverlay(double canvasWidth, double canvasHeight)
    {
        if (_loadedAudio is null || _loadedAudio.DurationSeconds <= 0)
        {
            return;
        }

        var startRatio = StartSlider.Value / _loadedAudio.DurationSeconds;
        var endRatio = EndSlider.Value / _loadedAudio.DurationSeconds;

        var leftX = canvasWidth * Math.Clamp(startRatio, 0, 1);
        var rightX = canvasWidth * Math.Clamp(endRatio, 0, 1);

        var outsideBrush = new SolidColorBrush(Color.FromArgb(120, 18, 24, 38));
        var leftRect = new Rectangle
        {
            Width = Math.Max(0, leftX),
            Height = canvasHeight,
            Fill = outsideBrush
        };
        var rightRect = new Rectangle
        {
            Width = Math.Max(0, canvasWidth - rightX),
            Height = canvasHeight,
            Fill = outsideBrush
        };

        Canvas.SetLeft(leftRect, 0);
        Canvas.SetTop(leftRect, 0);
        Canvas.SetLeft(rightRect, rightX);
        Canvas.SetTop(rightRect, 0);

        WaveformCanvas.Children.Add(leftRect);
        WaveformCanvas.Children.Add(rightRect);

        var markerBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB));
        var startMarker = new Line
        {
            X1 = leftX,
            X2 = leftX,
            Y1 = 0,
            Y2 = canvasHeight,
            Stroke = markerBrush,
            StrokeThickness = 1.5
        };
        var endMarker = new Line
        {
            X1 = rightX,
            X2 = rightX,
            Y1 = 0,
            Y2 = canvasHeight,
            Stroke = markerBrush,
            StrokeThickness = 1.5
        };

        WaveformCanvas.Children.Add(startMarker);
        WaveformCanvas.Children.Add(endMarker);
    }

    private void UpdateDragSelection(double x1, double x2)
    {
        if (_loadedAudio is null || _loadedAudio.DurationSeconds <= 0)
        {
            return;
        }

        var width = Math.Max(1, WaveformCanvas.ActualWidth);
        var minX = Math.Clamp(Math.Min(x1, x2), 0, width);
        var maxX = Math.Clamp(Math.Max(x1, x2), 0, width);

        var start = (minX / width) * _loadedAudio.DurationSeconds;
        var end = (maxX / width) * _loadedAudio.DurationSeconds;

        StartSlider.Value = start;
        EndSlider.Value = Math.Max(start, end);
        DrawWaveform();
    }

    private static float[] BuildWavePoints(AudioBuffer audio, int targetPointCount)
    {
        if (targetPointCount <= 0 || audio.Samples.Length == 0)
        {
            return [];
        }

        var frameCount = audio.Samples.Length / audio.Channels;
        if (frameCount == 0)
        {
            return [];
        }

        var step = Math.Max(1, frameCount / targetPointCount);
        var points = new List<float>(targetPointCount);

        for (var frameStart = 0; frameStart < frameCount; frameStart += step)
        {
            var frameEnd = Math.Min(frameStart + step, frameCount);
            var peak = 0f;

            for (var frame = frameStart; frame < frameEnd; frame++)
            {
                for (var channel = 0; channel < audio.Channels; channel++)
                {
                    var index = frame * audio.Channels + channel;
                    var value = Math.Abs(audio.Samples[index]);
                    if (value > peak)
                    {
                        peak = value;
                    }
                }
            }

            points.Add(Math.Min(1f, peak));
        }

        return points.ToArray();
    }

    private AudioBuffer TrimToSliderRange(AudioBuffer audio)
    {
        var startSeconds = Math.Clamp(StartSlider.Value, 0, audio.DurationSeconds);
        var endSeconds = Math.Clamp(EndSlider.Value, startSeconds, audio.DurationSeconds);

        var startFrame = (int)(startSeconds * audio.SampleRate);
        var endFrame = (int)(endSeconds * audio.SampleRate);

        return SliceByFrame(audio, startFrame, endFrame);
    }

    private static AudioBuffer TrimLeadingSilence(AudioBuffer audio, float threshold)
    {
        var frameCount = audio.Samples.Length / audio.Channels;
        var first = -1;

        for (var frame = 0; frame < frameCount; frame++)
        {
            if (FrameHasSignal(audio, frame, threshold))
            {
                first = frame;
                break;
            }
        }

        if (first < 0)
        {
            return new AudioBuffer([], audio.SampleRate, audio.Channels);
        }

        return SliceByFrame(audio, first, frameCount);
    }

    private static bool FrameHasSignal(AudioBuffer audio, int frame, float threshold)
    {
        for (var channel = 0; channel < audio.Channels; channel++)
        {
            var value = Math.Abs(audio.Samples[(frame * audio.Channels) + channel]);
            if (value >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static AudioBuffer SliceByFrame(AudioBuffer audio, int startFrame, int endFrame)
    {
        var frameCount = audio.Samples.Length / audio.Channels;
        startFrame = Math.Clamp(startFrame, 0, frameCount);
        endFrame = Math.Clamp(endFrame, startFrame, frameCount);

        var startIndex = startFrame * audio.Channels;
        var endIndex = endFrame * audio.Channels;
        var length = endIndex - startIndex;

        if (length <= 0)
        {
            return new AudioBuffer([], audio.SampleRate, audio.Channels);
        }

        var copied = new float[length];
        Array.Copy(audio.Samples, startIndex, copied, 0, length);
        return new AudioBuffer(copied, audio.SampleRate, audio.Channels);
    }

    private string? PickSavePath(string suffix)
    {
        var baseName = _selectedFilePath is null
            ? "hitsound"
            : Path.GetFileNameWithoutExtension(_selectedFilePath);

        var dialog = new SaveFileDialog
        {
            Filter = "WAV file (*.wav)|*.wav",
            FileName = $"{baseName}_{suffix}.wav",
            AddExtension = true,
            DefaultExt = "wav"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static void WriteWav(AudioBuffer audio, string destination)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.Channels);
        using var writer = new WaveFileWriter(destination, format);
        foreach (var sample in audio.Samples)
        {
            writer.WriteSample(sample);
        }
    }

    private bool EnsureAudioLoaded()
    {
        if (_loadedAudio is null)
        {
            SetStatus("No hitsound loaded.");
            return false;
        }

        return true;
    }

    private bool TryPlayAudio(AudioBuffer audio, string failureContext)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"osuhitsoundtuner_preview_{Guid.NewGuid():N}.wav");
            WriteWav(audio, tempPath);
            return TryPlayAudioFromPath(tempPath, failureContext, isTempSource: true);
        }
        catch (Exception ex)
        {
            StopPlayback();
            SetStatus($"{failureContext} Failed: {ex.Message}");
            return false;
        }
    }

    private bool TryPlayAudioFromPath(string path, string failureContext, bool isTempSource = false)
    {
        try
        {
            StopPlayback();

            if (isTempSource)
            {
                RegisterPlaybackTempFile(path);
            }

            var playbackPath = path;
            using (var probe = CreatePlaybackStream(path))
            {
                if (probe.TotalTime <= TimeSpan.FromSeconds(ShortClipThresholdSeconds))
                {
                    var prebufferedTempPath = Path.Combine(Path.GetTempPath(), $"osuhitsoundtuner_prebuffered_{Guid.NewGuid():N}.wav");
                    WritePrebufferedWav(path, prebufferedTempPath, TimeSpan.FromSeconds(ShortClipLeadInSeconds));
                    RegisterPlaybackTempFile(prebufferedTempPath);
                    playbackPath = prebufferedTempPath;
                }
            }

            _playbackStream = CreatePlaybackStream(playbackPath);
            _playbackOutput = new WaveOutEvent();
            _playbackOutput.Init(_playbackStream);
            _playbackOutput.PlaybackStopped += PlaybackOutput_PlaybackStopped;

            _playbackOutput.Play();
            return true;
        }
        catch (Exception ex)
        {
            StopPlayback();
            SetStatus($"{failureContext} Failed: {ex.Message}");
            return false;
        }
    }

    private static WaveStream CreatePlaybackStream(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".ogg" => new VorbisWaveReader(path),
            _ => new AudioFileReader(path)
        };
    }

    private static void WritePrebufferedWav(string sourcePath, string destination, TimeSpan leadIn)
    {
        using var source = CreatePlaybackStream(sourcePath);
        var sampleProvider = source.ToSampleProvider();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleProvider.WaveFormat.SampleRate, sampleProvider.WaveFormat.Channels);

        using var writer = new WaveFileWriter(destination, format);

        var leadInFrames = (int)(leadIn.TotalSeconds * format.SampleRate);
        var leadInSampleCount = leadInFrames * format.Channels;
        for (var i = 0; i < leadInSampleCount; i++)
        {
            writer.WriteSample(0f);
        }

        var buffer = new float[Math.Max(format.SampleRate / 2, 2048) * format.Channels];
        int read;
        while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                writer.WriteSample(buffer[i]);
            }
        }
    }

    private void RegisterPlaybackTempFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_playbackTempPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _playbackTempPaths.Add(path);
    }

    private void StopPlayback()
    {
        if (_playbackOutput is not null)
        {
            _playbackOutput.PlaybackStopped -= PlaybackOutput_PlaybackStopped;
            _playbackOutput.Dispose();
            _playbackOutput = null;
        }

        _playbackStream?.Dispose();
        _playbackStream = null;

        foreach (var tempPath in _playbackTempPaths)
        {
            if (!File.Exists(tempPath))
            {
                continue;
            }

            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }

        _playbackTempPaths.Clear();
    }

    private void PlaybackOutput_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        StopPlayback();
    }

    private void ReplaceAsEmptyAudio_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAudioLoaded() || !ReplaceEmptyAudioButton.IsEnabled)
        {
            return;
        }

        var selected = HitsoundComboBox.SelectedItem as HitsoundOption;
        if (selected is null)
        {
            return;
        }

        var sourceDirectory = Path.GetDirectoryName(selected.FilePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return;
        }

        var targetWavPath = Path.Combine(sourceDirectory, $"{selected.Key}.wav");
        var sampleRate = Math.Max(8000, _loadedAudio!.SampleRate);
        var channels = Math.Max(1, _loadedAudio!.Channels);
        WriteEmptyWav(targetWavPath, sampleRate, channels);

        SetStatus($"Replaced near-silent file as empty audio: {Path.GetFileName(targetWavPath)}");
        ReplaceEmptyAudioButton.IsEnabled = false;

        PopulateAvailableHitsoundsForSelectedSkin(showActionPrompt: false, preserveSelection: true);
    }

    private static void WriteEmptyWav(string destination, int sampleRate, int channels)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        using var writer = new WaveFileWriter(destination, format);
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private void UpdateTrimAnalysis(AudioBuffer audio)
    {
        if (IsAudioEmpty(audio))
        {
            SetTrimAnalysisWarning("Empty audio detected. No trimming is required.");
            return;
        }

        if (HasAudioAtStart(audio))
        {
            SetTrimAnalysisSuccess("0ms already has audio. This hitsound is already perfect and does not need start trimming.");
            return;
        }

        SetTrimAnalysisDanger("Start silence detected. This hitsound allows trimming at the beginning.");
    }

    private void SetTrimAnalysisSuccess(string message)
    {
        TrimAnalysisTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x80, 0x38));
        TrimAnalysisTextBlock.Text = message;
    }

    private void SetTrimAnalysisDanger(string message)
    {
        TrimAnalysisTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25));
        TrimAnalysisTextBlock.Text = message;
    }

    private void SetTrimAnalysisWarning(string message)
    {
        TrimAnalysisTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x60, 0x00));
        TrimAnalysisTextBlock.Text = message;
    }

    private void SetTrimAnalysisNeutral(string message)
    {
        TrimAnalysisTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x72));
        TrimAnalysisTextBlock.Text = message;
    }

    private static bool IsAudioEmpty(AudioBuffer audio)
    {
        if (audio.Samples.Length == 0 || audio.DurationSeconds <= 0)
        {
            return true;
        }

        return !audio.Samples.Any(sample => Math.Abs(sample) > 0.00001f);
    }

    private static bool IsNearSilentAudio(AudioBuffer audio)
    {
        if (IsAudioEmpty(audio))
        {
            return false;
        }

        var peak = audio.Samples.Max(sample => Math.Abs(sample));
        return peak < NearSilentPeakThreshold;
    }

    private static bool HasAudioAtStart(AudioBuffer audio)
    {
        var framesToCheck = Math.Min(
            (int)(audio.SampleRate * StartAudioWindowSeconds),
            Math.Max(1, audio.Samples.Length / Math.Max(1, audio.Channels)));

        for (var frame = 0; frame < framesToCheck; frame++)
        {
            if (FrameHasSignal(audio, frame, SilenceThreshold))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record SkinItem(string Path, string FolderName, string SkinName, string SkinAuthor, string DisplayLabel);

    private sealed record SuggestionItem(string Hitsound, string Message, bool IsActionable);

    private sealed record AnalysisListItem(string Key, string DisplayName, string Message)
    {
        public string Label => $"{DisplayName} - {Message}";
    }

    private sealed record SkinAnalysisResult(
        List<AnalysisListItem> PerfectItems,
        List<AnalysisListItem> RecommendedItems,
        List<AnalysisListItem> WarningItems)
    {
        public int ActionableCount => RecommendedItems.Count + WarningItems.Count;

        public List<SuggestionItem> GetSuggestions()
        {
            var suggestions = new List<SuggestionItem>();
            suggestions.AddRange(RecommendedItems.Select(item => new SuggestionItem(item.Key, item.Message, true)));
            suggestions.AddRange(WarningItems.Select(item => new SuggestionItem(item.Key, item.Message, true)));
            return suggestions;
        }
    }

    private sealed record HitsoundOption(string Key, string DisplayName, string FilePath);

    private sealed record AudioBuffer(float[] Samples, int SampleRate, int Channels)
    {
        public double DurationSeconds =>
            Channels == 0 || SampleRate == 0
                ? 0
                : Samples.Length / (double)(Channels * SampleRate);
    }
}
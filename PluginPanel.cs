using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RevitPluginTest
{
    // Command buttons + a text box that dispatches typed command names, plus
    // a log list. Both buttons and the text box go through InvokeCommand, the
    // same name-based dispatch path a future full console would use.
    // "help Name" or "? Name" prints that function's signature instead of
    // calling it; "help"/"?" alone lists every registered function.
    public sealed class PluginPanel : UserControl
    {
        private List<string>? _completionMatches;
        private int _completionIndex;
        private string _completionPrefix = string.Empty;
        private bool _completionAppendParen;

        private readonly List<string> _history = new();
        private int _historyIndex = -1; // -1 means "on the live line", not navigating history
        private string _pendingText = string.Empty;

        private readonly StackPanel _dynamicContainer = new();
        private readonly Dictionary<string, StackPanel> _dynamicSections = new();

        public PluginPanel()
        {
            DynamicPanel.Current = this;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            buttonPanel.Children.Add(CreateCommandButton("Reload Core", "ReloadCore"));
            buttonPanel.Children.Add(CreateCommandButton("Clear Log", "ClearLog"));
            Grid.SetRow(buttonPanel, 0);
            root.Children.Add(buttonPanel);

            var overlaySeparator = CreateSeparator("=== Debug Overlay ===");
            Grid.SetRow(overlaySeparator, 1);
            root.Children.Add(overlaySeparator);

            var overlayPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            overlayPanel.Children.Add(CreateCommandButton("Draw Origin", "DrawOrigin"));
            overlayPanel.Children.Add(CreateCommandButton("Clear Overlay", "ClearOverlay"));

            var visibilityCheckBox = new CheckBox
            {
                Content = "Overlay Visible",
                Margin = new Thickness(8, 4, 4, 4),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = OverlayState.UserWantsVisible
            };

            visibilityCheckBox.Checked += (sender, e) => OverlayState.UserWantsVisible = true;
            visibilityCheckBox.Unchecked += (sender, e) => OverlayState.UserWantsVisible = false;

            // Greyed out for perspective 3D views, where the overlay can't be
            // displayed at all - polled since IsViewSupported is written by
            // Core on its own tick, not something this control is notified of.
            var visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            visibilityTimer.Tick += (sender, e) => visibilityCheckBox.IsEnabled = OverlayState.IsViewSupported;
            visibilityTimer.Start();

            overlayPanel.Children.Add(visibilityCheckBox);

            Grid.SetRow(overlayPanel, 2);
            root.Children.Add(overlayPanel);

            // Populated at runtime via DynamicPanel.AddButton - Core registers
            // whatever sections/buttons it wants (fresh, every load/reload)
            // instead of them being hardcoded here.
            Grid.SetRow(_dynamicContainer, 3);
            root.Children.Add(_dynamicContainer);

            var commandRow = new DockPanel { Margin = new Thickness(4, 0, 4, 4) };

            var promptLabel = new TextBlock
            {
                Text = "==> ",
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(promptLabel, Dock.Left);
            commandRow.Children.Add(promptLabel);

            var commandBox = new TextBox();
            commandRow.Children.Add(commandBox);

            commandBox.PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Tab)
                {
                    HandleTabCompletion(commandBox);
                    e.Handled = true;
                    return;
                }

                // Any other key breaks the current completion cycle.
                _completionMatches = null;

                if (e.Key == Key.Up)
                {
                    NavigateHistory(commandBox, -1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Down)
                {
                    NavigateHistory(commandBox, 1);
                    e.Handled = true;
                }
                else
                {
                    // Editing the line manually exits history navigation, so the
                    // next Up starts fresh from the most recent entry again.
                    _historyIndex = -1;
                }
            };

            commandBox.KeyDown += (sender, e) =>
            {
                if (e.Key != Key.Enter)
                {
                    return;
                }

                var text = commandBox.Text.Trim();

                if (text.Length == 0)
                {
                    return;
                }

                AddToHistory(text);

                var (helpPrefix, remainder) = SplitHelpPrefix(text);

                if (helpPrefix.Length > 0)
                {
                    ShowHelp(remainder.Trim());
                    commandBox.Clear();
                    return;
                }

                if (!CommandText.TryParse(text, out var name, out var args, out var error))
                {
                    Logger.Log($"Parse error: {error}");
                    return;
                }

                Logger.Log($"> {text}");
                RevitPluginTestApplication.Current?.InvokeCommand(name, args);
                commandBox.Clear();
            };

            Grid.SetRow(commandRow, 4);
            root.Children.Add(commandRow);

            var log = new ListBox
            {
                ItemsSource = Logger.Entries,
                SelectionMode = SelectionMode.Extended
            };

            log.PreviewKeyDown += (sender, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control)
                {
                    return;
                }

                if (e.Key == Key.C)
                {
                    var text = string.Join(Environment.NewLine, log.SelectedItems.Cast<string>());

                    if (text.Length > 0)
                    {
                        Clipboard.SetText(text);
                    }

                    e.Handled = true;
                }
                else if (e.Key == Key.A)
                {
                    log.SelectAll();
                    e.Handled = true;
                }
            };

            Grid.SetRow(log, 5);
            root.Children.Add(log);

            Content = root;
        }

        private void HandleTabCompletion(TextBox commandBox)
        {
            if (_completionMatches == null)
            {
                var (helpPrefix, remainder) = SplitHelpPrefix(commandBox.Text);

                // Only complete the function name, not text inside the parens.
                if (remainder.Contains('('))
                {
                    return;
                }

                var scheduler = RevitPluginTestApplication.Current?.Scheduler;

                if (scheduler == null)
                {
                    return;
                }

                var prefix = remainder.Trim();

                _completionMatches = scheduler.GetFunctionNames()
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _completionIndex = -1;
                _completionPrefix = helpPrefix;
                _completionAppendParen = helpPrefix.Length == 0;
            }

            if (_completionMatches.Count == 0)
            {
                return;
            }

            _completionIndex = (_completionIndex + 1) % _completionMatches.Count;

            var suffix = _completionAppendParen ? "(" : "";
            commandBox.Text = _completionPrefix + _completionMatches[_completionIndex] + suffix;
            commandBox.CaretIndex = commandBox.Text.Length;
        }

        private void AddToHistory(string text)
        {
            if (_history.Count == 0 || _history[^1] != text)
            {
                _history.Add(text);
            }

            _historyIndex = -1;
        }

        // direction: -1 for Up (toward older entries), 1 for Down (toward newer).
        private void NavigateHistory(TextBox commandBox, int direction)
        {
            if (_history.Count == 0)
            {
                return;
            }

            if (_historyIndex == -1)
            {
                if (direction > 0)
                {
                    // Already on the live line - nothing further to go to.
                    return;
                }

                _pendingText = commandBox.Text;
                _historyIndex = _history.Count - 1;
            }
            else
            {
                var newIndex = _historyIndex + direction;

                if (newIndex >= _history.Count)
                {
                    _historyIndex = -1;
                    commandBox.Text = _pendingText;
                    commandBox.CaretIndex = commandBox.Text.Length;
                    return;
                }

                _historyIndex = Math.Max(0, newIndex);
            }

            commandBox.Text = _history[_historyIndex];
            commandBox.CaretIndex = commandBox.Text.Length;
        }

        private static void ShowHelp(string functionName)
        {
            var scheduler = RevitPluginTestApplication.Current?.Scheduler;

            if (scheduler == null)
            {
                return;
            }

            if (functionName.Length == 0)
            {
                foreach (var line in scheduler.DescribeAllFunctions())
                {
                    Logger.Log(line);
                }

                return;
            }

            Logger.Log(scheduler.DescribeFunction(functionName) ?? $"No function named '{functionName}'.");
        }

        // Recognizes a "help"/"help Name"/"?"/"? Name" prefix. Requires a
        // trailing space (or nothing after) so a real function literally
        // named e.g. "helper" isn't mistaken for the help keyword.
        private static (string Prefix, string Remainder) SplitHelpPrefix(string text)
        {
            if (text.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("help ", StringComparison.OrdinalIgnoreCase))
            {
                return ("help ", text.Length > 4 ? text.Substring(4).TrimStart() : "");
            }

            if (text.Equals("?") || text.StartsWith("? "))
            {
                return ("? ", text.Length > 1 ? text.Substring(1).TrimStart() : "");
            }

            return (string.Empty, text);
        }

        // section is auto-created (separator + button row) the first time
        // it's used, and reused for subsequent buttons in the same section.
        // Pass null/empty for a headerless row - no separator, just buttons.
        public void AddDynamicButton(string? section, string label, string commandName)
        {
            var key = section ?? string.Empty;

            if (!_dynamicSections.TryGetValue(key, out var sectionPanel))
            {
                if (!string.IsNullOrEmpty(key))
                {
                    _dynamicContainer.Children.Add(CreateSeparator($"=== {key} ==="));
                }

                sectionPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
                _dynamicContainer.Children.Add(sectionPanel);

                _dynamicSections[key] = sectionPanel;
            }

            sectionPanel.Children.Add(CreateCommandButton(label, commandName));
        }

        public void ClearDynamicWidgets()
        {
            _dynamicContainer.Children.Clear();
            _dynamicSections.Clear();
        }

        private static TextBlock CreateSeparator(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = Brushes.DimGray,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(4, 8, 4, 2)
            };
        }

        private static Button CreateCommandButton(string label, string commandName)
        {
            var button = new Button
            {
                Content = label,
                Margin = new Thickness(4)
            };

            button.Click += (sender, e) =>
            {
                Logger.Log($"> {commandName}()");
                RevitPluginTestApplication.Current?.InvokeCommand(commandName);
            };

            return button;
        }
    }
}

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
        private readonly Dictionary<string, StackPanel> _dynamicCurrentRow = new();
        private readonly HashSet<string> _dynamicSectionHeaders = new();

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
                Foreground = Brushes.Black,
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(2, 0, 2, 0)
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

            var logAutoScroll = true;

            log.Loaded += (sender, e) =>
            {
                var scrollViewer = FindScrollViewer(log);

                if (scrollViewer == null)
                {
                    return;
                }

                scrollViewer.ScrollChanged += (s2, e2) =>
                {
                    // A user-driven scroll leaves the extent unchanged; a new
                    // log entry growing the content does not - only the
                    // former should affect whether we keep auto-scrolling.
                    if (e2.ExtentHeightChange == 0)
                    {
                        logAutoScroll = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1.0;
                    }
                };
            };

            Logger.Entries.CollectionChanged += (sender, e) =>
            {
                if (logAutoScroll && log.Items.Count > 0)
                {
                    log.ScrollIntoView(log.Items[^1]);
                }
            };

            Content = root;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is ScrollViewer scrollViewer)
                {
                    return scrollViewer;
                }

                if (FindScrollViewer(child) is { } found)
                {
                    return found;
                }
            }

            return null;
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

        // section is auto-created (separator + widget row) the first time
        // it's used, and reused for subsequent widgets in the same section.
        // Pass null/empty for a headerless row - no separator, just widgets.
        public void AddDynamicButton(string? section, string label, string commandName)
        {
            GetOrCreateSection(section).Children.Add(CreateCommandButton(label, commandName));
        }

        // Same as AddDynamicButton, but its Content polls
        // OverlayState.IsAddingObstruction (250ms, same cadence as the
        // overlay visibility checkbox) and swaps to activeLabel while true.
        public void AddDynamicObstructionButton(string? section, string idleLabel, string activeLabel, string commandName)
        {
            var button = CreateCommandButton(idleLabel, commandName);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (sender, e) => button.Content = OverlayState.IsAddingObstruction ? activeLabel : idleLabel;
            timer.Start();

            GetOrCreateSection(section).Children.Add(button);
        }

        // Fires InvokeCommand(commandName, selectedOption) whenever the
        // selection changes. options are plain strings so Host never needs
        // to know about whatever enum/type Core is actually choosing between.
        public void AddDynamicDropdown(string? section, string label, IReadOnlyList<string> options, string commandName)
        {
            var sectionPanel = GetOrCreateSection(section);

            sectionPanel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            });

            var comboBox = new ComboBox
            {
                Margin = new Thickness(0, 4, 4, 4),
                MinWidth = 80,
                ItemsSource = options
            };

            if (options.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }

            comboBox.SelectionChanged += (sender, e) =>
            {
                if (comboBox.SelectedItem is string selected)
                {
                    RevitPluginTestApplication.Current?.InvokeCommand(commandName, selected);
                }
            };

            sectionPanel.Children.Add(comboBox);
        }

        // Fires InvokeCommand(commandName, isChecked) whenever the checkbox
        // is toggled - Core owns the actual setting, this just renders it
        // and forwards the change.
        public void AddDynamicCheckbox(string? section, string label, bool initialValue, string commandName)
        {
            var checkBox = new CheckBox
            {
                Content = label,
                Foreground = Brushes.White,
                Margin = new Thickness(4),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = initialValue
            };

            checkBox.Checked += (sender, e) => RevitPluginTestApplication.Current?.InvokeCommand(commandName, true);
            checkBox.Unchecked += (sender, e) => RevitPluginTestApplication.Current?.InvokeCommand(commandName, false);

            GetOrCreateSection(section).Children.Add(checkBox);
        }

        // Fires InvokeCommand(commandName, value) on every drag tick - cheap
        // since it just updates a stored setting, not a rebuild. label's
        // current value is appended live so the number is visible without a
        // separate readout control. step > 0 snaps the thumb to that
        // increment (e.g. 4 for a multiples-of-4 slider) rather than moving
        // continuously - the value sent to commandName still comes straight
        // from slider.Value, so whatever Core-side setter is on the other
        // end stays the authority on the actual stored value.
        public void AddDynamicSlider(string? section, string label, double min, double max, double initialValue, string commandName, double step = 0)
        {
            var sectionPanel = GetOrCreateSection(section);

            var valueLabel = new TextBlock
            {
                Text = $"{label} {initialValue:F1}",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = initialValue,
                Width = 100,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 4, 4)
            };

            if (step > 0)
            {
                slider.IsSnapToTickEnabled = true;
                slider.TickFrequency = step;
            }

            slider.ValueChanged += (sender, e) =>
            {
                valueLabel.Text = $"{label} {slider.Value:F1}";
                RevitPluginTestApplication.Current?.InvokeCommand(commandName, slider.Value);
            };

            sectionPanel.Children.Add(valueLabel);
            sectionPanel.Children.Add(slider);
        }

        private StackPanel GetOrCreateSection(string? section)
        {
            var key = section ?? string.Empty;

            if (_dynamicCurrentRow.TryGetValue(key, out var row))
            {
                return row;
            }

            if (!string.IsNullOrEmpty(key) && _dynamicSectionHeaders.Add(key))
            {
                _dynamicContainer.Children.Add(CreateSeparator($"=== {key} ==="));
            }

            row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            _dynamicContainer.Children.Add(row);

            _dynamicCurrentRow[key] = row;
            return row;
        }

        // Forces the next widget added to `section` onto a fresh row, without
        // repeating the section's header separator.
        public void AddDynamicNewLine(string? section)
        {
            _dynamicCurrentRow.Remove(section ?? string.Empty);
        }

        public void ClearDynamicWidgets()
        {
            _dynamicContainer.Children.Clear();
            _dynamicCurrentRow.Clear();
            _dynamicSectionHeaders.Clear();
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia.Views
{
    public partial class AvailableModels : Window
    {
        private readonly List<string> _allAvailableModels;
        private readonly List<string> _selectedModels;
        private readonly IDialogService _dialogs = new AvaloniaDialogService();

        private bool _filterHasError;

        public event Action<List<string>>? ModelsApplied;

        public AvailableModels()
            : this(Array.Empty<string>(), Array.Empty<string>())
        {
        }

        public AvailableModels(IEnumerable<string> availableModels, IEnumerable<string>? currentSelectedModels = null)
        {
            InitializeComponent();
            AddHandler(InputElement.KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
            _allAvailableModels = NormalizeDistinct(availableModels)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _selectedModels = (currentSelectedModels is not null
                    ? NormalizeDistinct(currentSelectedModels)
                    : AvailableModelsStorage.Load())
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RefreshAll();
        }

        private static List<string> NormalizeDistinct(IEnumerable<string>? models)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (models is null)
                return result;

            foreach (string? model in models)
            {
                string value = model?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                    result.Add(value);
            }

            return result;
        }

        private void SortSelectedModels() => _selectedModels.Sort(StringComparer.OrdinalIgnoreCase);

        private void RefreshAll()
        {
            RefreshSelectedList();
            RefreshAvailableList();
        }

        private void RefreshSelectedList()
        {
            SortSelectedModels();
            lbSelectedModels.ItemsSource = _selectedModels.ToList();
            txtSelectedCount.Text = $"{_selectedModels.Count} model(s)";
        }

        private void RefreshAvailableList()
        {
            IEnumerable<string> available = _allAvailableModels
                .Where(model => !_selectedModels.Contains(model, StringComparer.OrdinalIgnoreCase));
            List<string> filtered = ApplyFilter(available);

            lbAvailableModels.ItemsSource = filtered;
            txtAvailableCount.Text = _filterHasError
                ? "Invalid regex"
                : $"{filtered.Count} model(s)";
        }

        private List<string> ApplyFilter(IEnumerable<string> source)
        {
            _filterHasError = false;
            ClearFilterError();

            string filterText = tbFilter.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filterText))
                return source.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToList();

            if (chkUseRegex.IsChecked == true)
            {
                try
                {
                    var regex = new Regex(filterText, RegexOptions.IgnoreCase);
                    return source.Where(model => regex.IsMatch(model))
                        .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (ArgumentException ex)
                {
                    _filterHasError = true;
                    ShowFilterError(ex.Message);
                    return new List<string>();
                }
            }

            return source
                .Where(model => model.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ShowFilterError(string message)
        {
            tbFilter.BorderBrush = Brushes.OrangeRed;
            tbFilter.SetValue(ToolTip.TipProperty, $"Invalid regular expression: {message}");
        }

        private void ClearFilterError()
        {
            tbFilter.ClearValue(TemplatedControl.BorderBrushProperty);
            tbFilter.SetValue(ToolTip.TipProperty, "Type text to filter available models");
        }

        private void AddModelsToSelected(IEnumerable<string> modelsToAdd)
        {
            foreach (string model in modelsToAdd)
            {
                if (!_selectedModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                    _selectedModels.Add(model);
            }

            RefreshAll();
        }

        private void RemoveModelsFromSelected(IEnumerable<string> modelsToRemove)
        {
            var removeSet = new HashSet<string>(modelsToRemove, StringComparer.OrdinalIgnoreCase);
            _selectedModels.RemoveAll(removeSet.Contains);
            RefreshAll();
        }

        private List<string> GetVisibleAvailableModels() => lbAvailableModels.Items.Cast<string>().ToList();

        private List<string> GetSelectedModelsFromRightList() => lbSelectedModels.Items.Cast<string>().ToList();

        private static List<string> GetSelectedItems(ListBox listBox) =>
            listBox.SelectedItems?.OfType<string>().ToList() ?? new List<string>();

        private void Button_AddSelected_Click(object? sender, RoutedEventArgs e)
        {
            List<string> selected = GetSelectedItems(lbAvailableModels);
            if (selected.Count > 0)
                AddModelsToSelected(selected);
        }

        private void Button_AddAll_Click(object? sender, RoutedEventArgs e)
        {
            List<string> visibleModels = GetVisibleAvailableModels();
            if (visibleModels.Count > 0)
                AddModelsToSelected(visibleModels);
        }

        private void Button_RemoveSelected_Click(object? sender, RoutedEventArgs e)
        {
            List<string> selected = GetSelectedItems(lbSelectedModels);
            if (selected.Count > 0)
                RemoveModelsFromSelected(selected);
        }

        private void Button_RemoveAll_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedModels.Count == 0)
                return;

            _selectedModels.Clear();
            RefreshAll();
        }

        private async void Button_UseNow_Click(object? sender, RoutedEventArgs e)
        {
            List<string> modelsToUse = GetSelectedModelsFromRightList();
            if (modelsToUse.Count == 0)
            {
                await _dialogs.ShowMessageAsync(this, "Nothing to Use", "There are no selected models to use.", DialogSeverity.Information);
                return;
            }

            ModelsApplied?.Invoke(modelsToUse);
            await _dialogs.ShowMessageAsync(this, "Models Applied", $"{modelsToUse.Count} model(s) applied to the responses UI.", DialogSeverity.Information);
        }

        private async void Button_Save_model_list(object? sender, RoutedEventArgs e)
        {
            try
            {
                List<string> modelsToSave = GetSelectedModelsFromRightList();
                if (modelsToSave.Count == 0)
                {
                    await _dialogs.ShowMessageAsync(this, "Nothing to Save", "There are no selected models to save.\nUse Delete if you want to remove the saved file.", DialogSeverity.Information);
                    return;
                }

                AvailableModelsStorage.Save(modelsToSave);
                ModelsApplied?.Invoke(modelsToSave);
                await _dialogs.ShowMessageAsync(this, "Saved", $"Saved {modelsToSave.Count} model(s) to:\n{AvailableModelsStorage.FilePath}", DialogSeverity.Information);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowMessageAsync(this, "Save Error", $"Could not save model list:\n\n{ex.Message}", DialogSeverity.Error);
            }
        }

        private async void Button_Delete_model_list(object? sender, RoutedEventArgs e)
        {
            bool confirmed = await _dialogs.ConfirmAsync(this, "Delete Saved List", "Delete the saved model list file and clear the selected list?");
            if (!confirmed)
                return;

            try
            {
                AvailableModelsStorage.Delete();
                _selectedModels.Clear();
                RefreshAll();
                await _dialogs.ShowMessageAsync(this, "Deleted", "The saved model list was deleted.", DialogSeverity.Information);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowMessageAsync(this, "Delete Error", $"Could not delete the saved model list:\n\n{ex.Message}", DialogSeverity.Error);
            }
        }

        private void Button_Click(object? sender, RoutedEventArgs e) => Close();

        private void tbFilter_TextChanged(object? sender, TextChangedEventArgs e) => RefreshAvailableList();

        private void chkUseRegex_IsCheckedChanged(object? sender, RoutedEventArgs e) => RefreshAvailableList();

        private void lbAvailableModels_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (GetSelectedItems(lbAvailableModels).Count > 0)
                MoveSelectedAvailableToRightAndKeepFocus();
        }

        private void lbSelectedModels_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (GetSelectedItems(lbSelectedModels).Count > 0)
                MoveSelectedRightToLeftAndKeepFocus();
        }

        private void tbFilter_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (GetSelectedItems(lbAvailableModels).Count > 0)
                    MoveSelectedAvailableToRightAndKeepFocus();
                else if (lbAvailableModels.Items.Count > 0)
                {
                    lbAvailableModels.SelectedIndex = 0;
                    lbAvailableModels.Focus();
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (lbAvailableModels.Items.Count > 0)
                    FocusFirstItem(lbAvailableModels);

                e.Handled = true;
            }
        }

        private void lbAvailableModels_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                MoveSelectedAvailableToRightAndKeepFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                if (lbSelectedModels.Items.Count > 0)
                {
                    if (lbSelectedModels.SelectedIndex < 0)
                        FocusFirstItem(lbSelectedModels);
                    else
                        lbSelectedModels.Focus();
                }

                e.Handled = true;
            }
        }

        private void lbSelectedModels_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                MoveSelectedRightToLeftAndKeepFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                if (lbAvailableModels.Items.Count > 0)
                {
                    if (lbAvailableModels.SelectedIndex < 0)
                        FocusFirstItem(lbAvailableModels);
                    else
                        lbAvailableModels.Focus();
                }

                e.Handled = true;
            }
        }

        private static void FocusListBox(ListBox listBox, int preferredIndex = 0)
        {
            if (listBox.Items.Count == 0)
                return;

            int index = Math.Clamp(preferredIndex, 0, listBox.Items.Count - 1);
            listBox.SelectedIndex = index;
            if (listBox.SelectedItem is { } selectedItem)
                listBox.ScrollIntoView(selectedItem);
            listBox.Focus();
        }

        private static void FocusFirstItem(ListBox listBox) => FocusListBox(listBox);

        private void MoveSelectedAvailableToRightAndKeepFocus()
        {
            int oldIndex = lbAvailableModels.SelectedIndex;
            List<string> selected = GetSelectedItems(lbAvailableModels);
            if (selected.Count == 0)
                return;

            AddModelsToSelected(selected);

            if (lbAvailableModels.Items.Count > 0)
                FocusListBox(lbAvailableModels, Math.Min(oldIndex, lbAvailableModels.Items.Count - 1));
            else
                lbAvailableModels.Focus();
        }

        private void MoveSelectedRightToLeftAndKeepFocus()
        {
            int oldIndex = lbSelectedModels.SelectedIndex;
            List<string> selected = GetSelectedItems(lbSelectedModels);
            if (selected.Count == 0)
                return;

            RemoveModelsFromSelected(selected);

            if (lbSelectedModels.Items.Count > 0)
                FocusListBox(lbSelectedModels, Math.Min(oldIndex, lbSelectedModels.Items.Count - 1));
            else
                lbSelectedModels.Focus();
        }
        private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.App.Views;

public partial class PhoneModelWindow : Window
{
    public sealed record ModelRow(PhoneModel Model)
    {
        public string DisplayTitle => Model.Id;
        public string ResolutionText => Model.Screen.Points is { } pts
            ? $"{Model.Screen.Width} × {Model.Screen.Height}（{pts}pt）"
            : $"{Model.Screen.Width} × {Model.Screen.Height}";
    }

    private readonly AppController _controller;
    private readonly Action<bool> _themeHandler;

    public PhoneModelWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        WindowPlacementHelper.ApplyMicaBackdrop(this, _controller.IsEffectiveDarkTheme);
        _themeHandler = isDark => Dispatcher.BeginInvoke(() => WindowPlacementHelper.ApplyMicaBackdrop(this, isDark));
        _controller.ThemeChanged += _themeHandler;
        Closed += (_, _) => _controller.ThemeChanged -= _themeHandler;
        RefreshList(string.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void RefreshList(string query)
    {
        var cur = _controller.Config.Phone;
        CurrentLabel.Text = $"{cur.Model ?? "自定义"}（{cur.Screen.Width} × {cur.Screen.Height}）";

        var rows = new List<ModelRow>();
        foreach (var brand in _controller.Catalog.Search(query))
        {
            foreach (var m in brand.Models)
                rows.Add(new ModelRow(m));
        }
        ModelList.ItemsSource = rows;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        RefreshList(SearchBox.Text);

    private void OnSelectRow(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: ModelRow row })
        {
            _controller.SelectPhone(PhoneChoice.FromModel(row.Model));
            Close();
        }
    }

    private void OnModelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModelList.SelectedItem is ModelRow row)
        {
            _controller.SelectPhone(PhoneChoice.FromModel(row.Model));
            Close();
        }
    }

    private void OnApplyCustom(object sender, RoutedEventArgs e)
    {
        int? w = int.TryParse(CustomWidthBox.Text.Trim(), out int wi) ? wi : null;
        int? h = int.TryParse(CustomHeightBox.Text.Trim(), out int hi) ? hi : null;
        var custom = PhoneScreenSpecData.Custom(w, h);
        if (custom is null)
        {
            System.Windows.MessageBox.Show("请输入有效的正整数分辨率或比例（1 ~ 20000）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _controller.SelectPhone(new PhoneChoice(null, custom));
        Close();
    }
}

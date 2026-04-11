using System.Windows;
using System.Windows.Input;

namespace NetX.App.Dialogs;

public partial class InputDialog : Window
{
    public string ResponseText { get; private set; } = "";

    public InputDialog(string title, string message, string defaultValue = "")
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        InputTextBox.Text = defaultValue;

        Loaded += (s, e) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = InputTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OKButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            CancelButton_Click(sender, e);
        }
    }
}

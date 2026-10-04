using System.Diagnostics;
using System.Windows;

namespace DochkaDock;

public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/michaeldallariva/DochkaDock";
    private const string LicenseUrl = "https://polyformproject.org/licenses/noncommercial/1.0.0";

    public AboutWindow()
    {
        InitializeComponent();
    }

    private void GitHubLink_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

    private void LicenseLink_Click(object sender, RoutedEventArgs e) => OpenUrl(LicenseUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort only — no browser available shouldn't crash the dialog.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

namespace InterviewTrace.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\InterviewTrace.Desktop", out bool created);
        if (!created)
        {
            MessageBox.Show("InterviewTrace 已经在运行。", "InterviewTrace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.Run(new MainForm());
    }
}

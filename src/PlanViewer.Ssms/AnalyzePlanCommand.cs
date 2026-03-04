using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace PlanViewer.Ssms
{
    internal sealed class AnalyzePlanCommand
    {
        public static readonly Guid CommandSet = new Guid("BC78C8B3-E030-4759-AE25-EE3B093AB4C8");
        public const int CommandId = 0x0100;

        private readonly AsyncPackage _package;

        private AnalyzePlanCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
                as OleMenuCommandService;
            if (commandService != null)
            {
                new AnalyzePlanCommand(package, commandService);
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                string planXml = ShowPlanHelper.GetShowPlanXml();
                if (string.IsNullOrEmpty(planXml))
                {
                    ShowError("Could not extract the execution plan XML.\n\n" +
                              "Make sure you right-click on an execution plan window.");
                    return;
                }

                string tempFile = AppLauncher.SavePlanToTemp(planXml);
                bool launched = AppLauncher.LaunchApp(tempFile);

                if (!launched)
                {
                    // App not found — ask user to locate it once
                    var result = System.Windows.Forms.MessageBox.Show(
                        "SQL Performance Studio was not found.\n\n" +
                        "Would you like to locate it? The path will be saved for next time.",
                        "SQL Performance Studio",
                        System.Windows.Forms.MessageBoxButtons.YesNo,
                        System.Windows.Forms.MessageBoxIcon.Question);

                    if (result == System.Windows.Forms.DialogResult.Yes)
                    {
                        string appPath = AppLauncher.BrowseForApp();
                        if (appPath != null)
                        {
                            // Try again now that the path is saved to registry
                            if (!AppLauncher.LaunchApp(tempFile))
                            {
                                ShowError("Could not launch SQL Performance Studio from:\n" + appPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError("Error opening plan in SQL Performance Studio:\n\n" + ex.Message);
            }
        }

        private void ShowError(string message)
        {
            System.Windows.Forms.MessageBox.Show(
                message,
                "SQL Performance Studio",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
    }
}

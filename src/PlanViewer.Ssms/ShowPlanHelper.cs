using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;

namespace PlanViewer.Ssms
{
    /// <summary>
    /// Extracts execution plan XML from the SSMS ShowPlan window via reflection.
    /// Based on Martin Smith's approach: https://stackoverflow.com/a/73614350
    /// </summary>
    internal static class ShowPlanHelper
    {
        private const string ShowPlanControlTypeName =
            "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ShowPlan.ShowPlanControl";

        /// <summary>
        /// Extracts the plan XML from the currently focused SSMS execution plan window.
        /// </summary>
        public static string GetShowPlanXml()
        {
            // Get the focused control and walk to the root
            var focused = Control.FromHandle(GetFocus());
            if (focused == null)
                return null;

            var root = focused;
            while (root.Parent != null)
                root = root.Parent;

            // Find all ShowPlanControl instances in the control tree
            var showPlanControlType = FindShowPlanControlType();
            if (showPlanControlType == null)
                return null;

            var showPlanControls = new List<Control>();
            FindControlsOfType(root, showPlanControlType, showPlanControls);

            if (showPlanControls.Count == 0)
                return null;

            // Call GetShowPlanXml() via reflection on the first ShowPlanControl found
            var method = showPlanControlType.GetMethod("GetShowPlanXml",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (method == null)
                return null;

            var result = method.Invoke(showPlanControls[0], null);
            return result as string;
        }

        private static Type FindShowPlanControlType()
        {
            // Search all loaded assemblies for the ShowPlanControl type
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(ShowPlanControlTypeName);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Skip assemblies that can't be inspected
                }
            }

            return null;
        }

        private static void FindControlsOfType(Control parent, Type targetType, List<Control> results)
        {
            if (targetType.IsInstanceOfType(parent))
            {
                results.Add(parent);
                return;
            }

            foreach (Control child in parent.Controls)
            {
                FindControlsOfType(child, targetType, results);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();
    }
}

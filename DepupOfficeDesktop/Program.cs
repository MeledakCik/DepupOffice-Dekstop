using System;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;
using DepupOfficeDesktop.Forms;

namespace DepupOfficeDesktop
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            AppStorage.Initialize();

            string? initialFile = null;
            if (args.Length > 0)
            {
                if (!File.Exists(args[0]))
                    MessageBox.Show($"Berkas tidak ditemukan:\n{args[0]}", "Depup Office", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (!AppStorage.IsSupportedDocument(args[0]))
                    MessageBox.Show(AppStorage.UnsupportedDocumentMessage(args[0]), "Depup Office", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    initialFile = args[0];
            }
            Application.Run(new MainForm(initialFile));
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Console
    {
        private ButtonTS btnGpuWaterfallTest;
        private bool traceTestButton;
        private bool gpuTraceTestButton;

        private void InitializeGpuWaterfallTestButton()
        {
            // This control belongs only to the opt-in diagnostic launcher.
            if (Environment.GetEnvironmentVariable("THETIS_DISPLAY_TEST_BUTTON") != "1")
                return;
            traceTestButton = Environment.GetEnvironmentVariable("THETIS_TRACE_TEST_BUTTON") == "1";
            gpuTraceTestButton = Environment.GetEnvironmentVariable("THETIS_GPU_TRACE_TEST_BUTTON") == "1";

            int top = chkFWCATU.Bottom + 1;
            btnGpuWaterfallTest = new ButtonTS
            {
                Name = "btnGpuWaterfallTest",
                Location = new Point(comboDisplayMode.Left, top),
                Size = new Size(comboDisplayMode.Width,
                    Math.Max(18, panelDisplay2.ClientSize.Height - top - 1)),
                TabStop = false
            };
            btnGpuWaterfallTest.Click += (sender, args) =>
            {
                if (gpuTraceTestButton)
                    Display.GpuPanTraceEnabled = !Display.GpuPanTraceEnabled;
                else if (traceTestButton)
                    Display.BatchPanTraceEnabled = !Display.BatchPanTraceEnabled;
                else
                    Display.GpuWaterfallRowEnabled = !Display.GpuWaterfallRowEnabled;
                UpdateGpuWaterfallTestButton();
            };
            panelDisplay2.Controls.Add(btnGpuWaterfallTest);
            btnGpuWaterfallTest.BringToFront();
            UpdateGpuWaterfallTestButton();
        }

        private void UpdateGpuWaterfallTestButton()
        {
            if (btnGpuWaterfallTest == null) return;
            if (gpuTraceTestButton)
            {
                bool gpuTrace = Display.GpuPanTraceEnabled;
                btnGpuWaterfallTest.Text = gpuTrace ? "TESTE LINHA GPU" : "TESTE LOTE";
                toolTip1.SetToolTip(btnGpuWaterfallTest,
                    gpuTrace ? "Linha 2D formada pela GPU. Clique para comparar com o lote Direct2D."
                        : "Linha 2D em lote Direct2D. Clique para comparar com a GPU.");
                return;
            }
            if (traceTestButton)
            {
                bool batch = Display.BatchPanTraceEnabled;
                btnGpuWaterfallTest.Text = batch ? "TESTE LOTE" : "TESTE ATUAL";
                toolTip1.SetToolTip(btnGpuWaterfallTest,
                    batch ? "Tracado 2D em lote. Clique para comparar com o atual, sem reiniciar."
                          : "Tracado 2D atual. Clique para comparar com lote, sem reiniciar.");
                return;
            }
            bool gpu = Display.GpuWaterfallRowEnabled;
            btnGpuWaterfallTest.Text = gpu ? "TESTE GPU" : "TESTE CPU";
            toolTip1.SetToolTip(btnGpuWaterfallTest,
                gpu ? "Waterfall 2D colorido pela GPU. Clique para comparar com CPU, sem reiniciar."
                    : "Waterfall 2D colorido pela CPU. Clique para comparar com GPU, sem reiniciar.");
        }
    }
}

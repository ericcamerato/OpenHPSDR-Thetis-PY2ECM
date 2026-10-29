using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Console
    {
        private bool ultrawideMode;
        private readonly Dictionary<Control, Rectangle> ultrawideOriginalBounds =
            new Dictionary<Control, Rectangle>();

        public bool UltrawideAvailable
        {
            get
            {
                if (collapsedDisplay) return false;
                Rectangle bounds = Screen.FromControl(this).Bounds;
                return bounds.Width >= 2560 && bounds.Height > 0 &&
                    (double)bounds.Width / bounds.Height >= 2.3;
            }
        }

        public bool UltrawideMode
        {
            get { return ultrawideMode; }
            set
            {
                bool enabled = value && UltrawideAvailable;
                if (ultrawideMode == enabled) return;
                ultrawideMode = enabled;
                if (IsHandleCreated) ResizeConsole(h_delta, v_delta);
            }
        }

        private void InitializeUltrawideMode()
        {
            Shown += (sender, args) =>
            {
                if (ultrawideMode) ResizeConsole(h_delta, v_delta);
            };
            LocationChanged += (sender, args) =>
            {
                if (IsHandleCreated && ultrawideMode && !UltrawideAvailable)
                    UltrawideMode = false;
            };
        }

        private void PlaceUltrawide(Control c, int x, int y, int width = -1, int height = -1)
        {
            if (c == null) return;
            if (!ultrawideOriginalBounds.ContainsKey(c))
                ultrawideOriginalBounds.Add(c, c.Bounds);
            c.Bounds = new Rectangle(x, y, width < 0 ? c.Width : width,
                height < 0 ? c.Height : height);
        }

        private void RestoreUltrawidePositions()
        {
            foreach (KeyValuePair<Control, Rectangle> entry in ultrawideOriginalBounds)
                if (!entry.Key.IsDisposed) entry.Key.Bounds = entry.Value;
            ultrawideOriginalBounds.Clear();
        }

        private void PlaceSoundPair(Control label, Control slider, int col, int row)
        {
            int x = 8 + 118 * col;
            // Centre the two normal audio rows against panelVFO's visible
            // SPLT-to-VAC content (roughly y=5..144), not the empty deck.
            // Tune keeps a reserved third row when its slider is enabled.
            int y = row == 0 ? 30 : row == 1 ? 79 : 122;
            PlaceUltrawide(label, x, y, 112);
            PlaceUltrawide(slider, x, y + (row == 2 ? 16 : 17), 112);
        }

        private void LayoutUltrawideSoundControls()
        {
            PlaceSoundPair(lblAF, ptbAF, 0, 0);
            PlaceSoundPair(lblRX1AF, ptbRX1AF, 1, 0);
            PlaceSoundPair(lblRX2AF, ptbRX2AF, 2, 0);
            PlaceSoundPair(lblRF, ptbRF, 0, 1);
            PlaceSoundPair(lblPWR, ptbPWR, 1, 1);
            PlaceSoundPair(lblTune, ptbTune, 1, 2);
            // AGC and A-ATT complete the third column directly below RX2 AF.
            PlaceUltrawide(lblAGC, 244, 79);
            PlaceUltrawide(comboAGC, 244, 98);
            PlaceUltrawide(lblPreamp, 304, 79);
            PlaceUltrawide(comboPreamp, 304, 98);
            PlaceUltrawide(udRX1StepAttData, 304, 98);
            PlaceUltrawide(udTXStepAttData, 304, 98);
            PlaceUltrawide(pbAutoAttWarningRX1, 344, 124);
        }

        private void ApplyUltrawideLayout()
        {
            if (!ultrawideMode || !UltrawideAvailable) return;

            // Keep the original vertical placement and height. Fill both side rails.
            PlaceUltrawide(panelDisplay, 0, panelDisplay.Top, ClientSize.Width,
                panelDisplay.Height);

            int top = panelDisplay.Bottom + 7;
            int free = Math.Max(0, ClientSize.Width - 124 - 8 - 1705);
            int gap = free / 7;
            int options = 124;
            int sound = options + 118 + gap;
            int vfo = sound + 370 + gap;
            int dsp = vfo + 130 + gap;
            int phone = dsp + 232 + gap;
            int band = phone + 336 + gap;
            int mode = band + 173 + gap;
            int filter = mode + 173 + gap;

            PlaceUltrawide(panelPower, options, top);
            PlaceUltrawide(panelOptions, options, top + 40);
            PlaceUltrawide(chkSquelch, options + 16, top + 179);
            PlaceUltrawide(ptbSquelch, options + 6, top + 202);
            PlaceUltrawide(picSquelch, options + 15, top + 223);
            PlaceUltrawide(panelSoundControls, sound, top, 370, 165);
            LayoutUltrawideSoundControls();
            PlaceUltrawide(panelVFO, vfo, top);
            PlaceUltrawide(panelDSP, dsp, top);
            PlaceUltrawide(panelDisplay2, dsp + 122, top);
            PlaceUltrawide(panelMultiRX, phone, top + 165);
            PlaceUltrawide(panelModeSpecificPhone, phone, top);
            PlaceUltrawide(panelModeSpecificCW, phone, top);
            PlaceUltrawide(panelModeSpecificDigital, phone, top);
            PlaceUltrawide(panelModeSpecificFM, phone, top);
            PlaceUltrawide(panelBandHF, band, top);
            PlaceUltrawide(panelBandGEN, band, top);
            PlaceUltrawide(panelBandVHF, band, top);
            PlaceUltrawide(panelMode, mode, top);
            PlaceUltrawide(panelFilter, filter, top);

            // RX2 has a separate aligned row, below the primary controls.
            int second = top + 165;
            int x = sound;
            PlaceUltrawide(panelRX2Power, x, second);
            x += 78;
            PlaceUltrawide(panelRX2Mixer, x, second);
            x += 78;
            int dx = x - panelRX2RF.Left;
            int dy = second - panelRX2RF.Top;
            PlaceUltrawide(panelRX2RF, x, second);
            PlaceUltrawide(chkRX2Squelch, chkRX2Squelch.Left + dx, chkRX2Squelch.Top + dy);
            PlaceUltrawide(ptbRX2Squelch, ptbRX2Squelch.Left + dx, ptbRX2Squelch.Top + dy);
            PlaceUltrawide(picRX2Squelch, picRX2Squelch.Left + dx, picRX2Squelch.Top + dy);
            x += 118;
            PlaceUltrawide(panelRX2DSP, x, second);
            x += 126;
            PlaceUltrawide(panelRX2Display, x, second);
            x += 110;
            PlaceUltrawide(panelRX2Mode, x, second);
            x += 170;
            PlaceUltrawide(panelRX2Filter, x, second);
            PlaceUltrawide(grpRX2Meter, band + 16, second);
            PlaceUltrawide(grpDisplaySplit, phone + 240, second + 4);
        }
    }
}

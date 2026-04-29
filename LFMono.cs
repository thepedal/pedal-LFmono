// LFMono — Low-Frequency Mono Effect Machine for ReBuzz
//
// Converts all stereo signal below a crossover frequency to mono.
// Uses a 4th-order Linkwitz-Riley crossover (two cascaded Butterworth biquads)
// so that LP + HP sums to a flat magnitude response — fully transparent.
//
// Build: net10.0-windows10.0.26100.0, x64
// References: BuzzGUI.Interfaces.dll, BuzzGUI.Common.dll (copy from ReBuzz bin)

using System;
using Buzz.MachineInterface;

namespace LFMono
{
    // ── Serialised state (saved with the song) ────────────────────────────────
    public class LFMonoState
    {
        public int CrossoverHz { get; set; } = 230;
        public int LFLevel     { get; set; } = 100;
        public int HFLevel     { get; set; } = 100;
    }

    // ── Machine ───────────────────────────────────────────────────────────────
    [MachineDecl(Name = "Pedal LFMono", ShortName = "LFMono", Author = "Custom", MaxTracks = 0)]
    public class LFMonoMachine : IBuzzMachine
    {
        readonly IBuzzMachineHost host;

        // Constructor — ReBuzz passes the host here
        public LFMonoMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        // ── Saved state ───────────────────────────────────────────────────────
        LFMonoState state = new LFMonoState();
        public LFMonoState MachineState
        {
            get => state;
            set { state = value; _sampleRate = 0; } // force coeff recalc on load
        }

        // ── Parameters ────────────────────────────────────────────────────────

        [ParameterDecl(Name = "Crossover Hz", Description = "Signal below this frequency is summed to mono",
                       MinValue = 40, MaxValue = 800, DefValue = 230)]
        public int CrossoverHz
        {
            get => state.CrossoverHz;
            set { state.CrossoverHz = value; _sampleRate = 0; } // force recalc
        }

        [ParameterDecl(Name = "LF Level %", Description = "Output level of the mono low band (100 = unity)",
                       MinValue = 0, MaxValue = 200, DefValue = 100)]
        public int LFLevel
        {
            get => state.LFLevel;
            set => state.LFLevel = value;
        }

        [ParameterDecl(Name = "HF Level %", Description = "Output level of the stereo high band (100 = unity)",
                       MinValue = 0, MaxValue = 200, DefValue = 100)]
        public int HFLevel
        {
            get => state.HFLevel;
            set => state.HFLevel = value;
        }

        // ── DSP state: two cascaded biquad stages x two channels x LP and HP ─
        // [stage 0|1][channel L=0, R=1]
        double[,] lpx1 = new double[2,2], lpx2 = new double[2,2];
        double[,] lpy1 = new double[2,2], lpy2 = new double[2,2];
        double[,] hpx1 = new double[2,2], hpx2 = new double[2,2];
        double[,] hpy1 = new double[2,2], hpy2 = new double[2,2];

        double b0lp, b1lp, b2lp, a1lp, a2lp;
        double b0hp, b1hp, b2hp, a1hp, a2hp;

        int _sampleRate    = 0; // 0 = needs recalc
        int _silenceFrames = 0; // consecutive silent frames counted

        // ── Work (called on audio thread) ─────────────────────────────────────
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // Detect sample rate changes (MasterInfo only valid inside Work)
            int sr = host.MasterInfo.SamplesPerSec;
            if (sr != _sampleRate)
            {
                _sampleRate = sr;
                RecalcCoefficients();
            }

            if (mode == WorkModes.WM_NOIO) return false;

            // Check if the input buffer is silent. Returning false tells ReBuzz
            // we produced no output, allowing it to skip us and anything downstream
            // — which is the real cause of high CPU when upstream machines are muted.
            // We allow ~100ms of drain time so filter tails aren't cut off abruptly.
            bool inputSilent = true;
            for (int i = 0; i < n; i++)
            {
                if (input[i].L != 0f || input[i].R != 0f) { inputSilent = false; break; }
            }

            if (inputSilent)
            {
                _silenceFrames += n;
                if (_silenceFrames >= _sampleRate / 10) // 100ms drain elapsed
                {
                    ClearState();
                    return false; // signal to ReBuzz: no output, skip downstream
                }
                // Still within drain window — keep processing so the filter tail
                // decays naturally rather than clicking off
            }
            else
            {
                _silenceFrames = 0;
            }

            float lfGain = state.LFLevel * 0.01f;
            float hfGain = state.HFLevel * 0.01f;

            for (int i = 0; i < n; i++)
            {
                double sL = input[i].L;
                double sR = input[i].R;

                // Stage 1
                double lpL1 = BiquadLP(0, 0, sL), lpR1 = BiquadLP(0, 1, sR);
                double hpL1 = BiquadHP(0, 0, sL), hpR1 = BiquadHP(0, 1, sR);

                // Stage 2 -> completes LR4
                double lpL  = BiquadLP(1, 0, lpL1), lpR  = BiquadLP(1, 1, lpR1);
                double hpL  = BiquadHP(1, 0, hpL1), hpR  = BiquadHP(1, 1, hpR1);

                // Sum low band to mono
                double mono = (lpL + lpR) * 0.5;

                output[i].L = (float)(mono * lfGain + hpL * hfGain);
                output[i].R = (float)(mono * lfGain + hpR * hfGain);
            }

            return true;
        }

        // ── Biquad helpers (Direct Form I) ────────────────────────────────────
        double BiquadLP(int stage, int ch, double x)
        {
            double y = b0lp*x + b1lp*lpx1[stage,ch] + b2lp*lpx2[stage,ch]
                               - a1lp*lpy1[stage,ch] - a2lp*lpy2[stage,ch];
            lpx2[stage,ch] = lpx1[stage,ch]; lpx1[stage,ch] = x;
            lpy2[stage,ch] = lpy1[stage,ch]; lpy1[stage,ch] = y;
            return y;
        }

        double BiquadHP(int stage, int ch, double x)
        {
            double y = b0hp*x + b1hp*hpx1[stage,ch] + b2hp*hpx2[stage,ch]
                               - a1hp*hpy1[stage,ch] - a2hp*hpy2[stage,ch];
            hpx2[stage,ch] = hpx1[stage,ch]; hpx1[stage,ch] = x;
            hpy2[stage,ch] = hpy1[stage,ch]; hpy1[stage,ch] = y;
            return y;
        }

        // ── Coefficient calculation ───────────────────────────────────────────
        // Butterworth 2nd-order biquad pair. Two cascaded stages = LR4.
        void RecalcCoefficients()
        {
            double fc = Math.Clamp(state.CrossoverHz, 10.0, _sampleRate * 0.45);
            double w0    = 2.0 * Math.PI * fc / _sampleRate;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);
            const double Q = 0.7071067811865476; // 1/sqrt(2) -> Butterworth
            double alpha   = sinW0 / (2.0 * Q);

            // Low-pass
            double a0lp = 1.0 + alpha;
            b0lp = (1.0 - cosW0) * 0.5 / a0lp;
            b1lp = (1.0 - cosW0)       / a0lp;
            b2lp = (1.0 - cosW0) * 0.5 / a0lp;
            a1lp = -2.0 * cosW0        / a0lp;
            a2lp = (1.0 - alpha)        / a0lp;

            // High-pass
            double a0hp = 1.0 + alpha;
            b0hp =  (1.0 + cosW0) * 0.5 / a0hp;
            b1hp = -(1.0 + cosW0)       / a0hp;
            b2hp =  (1.0 + cosW0) * 0.5 / a0hp;
            a1hp = -2.0 * cosW0         / a0hp;
            a2hp = (1.0 - alpha)         / a0hp;

            ClearState();
        }

        void ClearState()
        {
            Array.Clear(lpx1,0,4); Array.Clear(lpx2,0,4);
            Array.Clear(lpy1,0,4); Array.Clear(lpy2,0,4);
            Array.Clear(hpx1,0,4); Array.Clear(hpx2,0,4);
            Array.Clear(hpy1,0,4); Array.Clear(hpy2,0,4);
        }

        // ── Optional stubs ────────────────────────────────────────────────────
        public void Stop() { }
    }
}

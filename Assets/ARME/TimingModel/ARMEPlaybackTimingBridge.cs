using UnityEngine;

namespace ARMETiming
{
    /// <summary>
    /// Pure C# port of AMUSER's ARMEPlaybackTimingBridge — needs NO native library.
    ///
    /// Turns a desired beat interval into a per-onset-segment playback rate the way the
    /// AMUSER app does:
    ///
    ///   rate = scoreInterval / targetInterval        (clamped 0.5–2.0)
    ///
    /// where <c>targetInterval</c> comes from the ARME Timing Model's period-correction
    /// calculation. That calculation is plain arithmetic, so it's reimplemented here in
    /// managed code (a faithful port of ARMETimingModel::calculatePeriodCorrectionOnset).
    /// AMUSER invokes it with alpha = beta = 0 and two identical previous onsets, which makes
    /// it a clean pass-through (the interval is returned unchanged); the cross-player coupling
    /// in the Unity controller is applied separately (the agentic ensemble-mean blend), exactly
    /// as in AMUSER. Keeping this native-free means it can never fail to load.
    /// </summary>
    public sealed class ARMEPlaybackTimingBridge
    {
        /// <summary>
        /// Run <paramref name="desiredInterval"/> through the timing model exactly as AMUSER does
        /// and return the resulting target interval (seconds). With AMUSER's degenerate
        /// parameters this equals <paramref name="desiredInterval"/>.
        /// </summary>
        public float PredictTargetInterval(float currentOnset, float desiredInterval)
        {
            if (desiredInterval <= 0f)
                return desiredInterval;

            // AMUSER: playerIndex 0, previousOnsets [onset, onset], alpha/beta [0,0],
            //         baseInterval = desiredInterval, all noise 0.
            float predictedOnset = CalculatePeriodCorrectionOnset(
                0,
                new[] { currentOnset, currentOnset },
                new[] { 0f, 0f },
                new[] { 0f, 0f },
                desiredInterval,
                0f, 0f, 0f);

            float interval = predictedOnset - currentOnset;
            if (!float.IsNaN(interval) && !float.IsInfinity(interval) && interval > 0f)
                return interval;

            return desiredInterval;
        }

        /// <summary>
        /// AMUSER's <c>calculateAVPlaybackRate</c>: the playback-rate multiplier to apply to a
        /// part across the segment [currentOnset, nextOnset] so the next recorded onset lands
        /// one <paramref name="desiredInterval"/> after the current one.
        ///   rate &gt; 1 → play faster,  rate &lt; 1 → play slower.
        /// Always returns a finite value in [0.5, 2.0]; never throws.
        /// </summary>
        public float CalculateAVPlaybackRate(float currentOnset, float playbackOnsetDelta, float desiredInterval)
        {
            if (playbackOnsetDelta <= 0f || desiredInterval <= 0f)
                return 1f;

            float target = PredictTargetInterval(currentOnset, desiredInterval);
            if (target <= 0f)
                target = desiredInterval;

            float rate = playbackOnsetDelta / target;
            if (float.IsNaN(rate) || float.IsInfinity(rate) || rate <= 0f)
                return 1f;

            return Mathf.Clamp(rate, 0.5f, 2.0f);
        }

        /// <summary>
        /// Faithful managed port of <c>ARMETimingModel::calculatePeriodCorrectionOnset</c>:
        ///   correctedInterval = baseInterval − Σ_j β_j·(onset_i − onset_j)        (period correction)
        ///   nextOnset = onset_i + correctedInterval + timeKeeperNoise + motorNoise
        ///             − previousMotorNoise − Σ_j α_j·(onset_i − onset_j)          (phase correction)
        /// </summary>
        public static float CalculatePeriodCorrectionOnset(
            int playerIndex,
            float[] previousOnsets,
            float[] alpha,
            float[] beta,
            float baseInterval,
            float previousMotorNoise,
            float timeKeeperNoise,
            float motorNoise)
        {
            int n = previousOnsets != null ? previousOnsets.Length : 0;
            if (n < 2 || playerIndex < 0 || playerIndex >= n || alpha == null || beta == null)
                return 0f;

            float self = previousOnsets[playerIndex];

            float periodCorrection = 0f;   // Σ β_j·asynchrony
            float phaseCorrection = 0f;    // Σ α_j·asynchrony
            for (int j = 0; j < n; j++)
            {
                if (j == playerIndex)
                    continue;
                float asynchrony = self - previousOnsets[j];
                if (j < beta.Length) periodCorrection += beta[j] * asynchrony;
                if (j < alpha.Length) phaseCorrection += alpha[j] * asynchrony;
            }

            float correctedInterval = baseInterval - periodCorrection;
            return self + correctedInterval + timeKeeperNoise + motorNoise - previousMotorNoise - phaseCorrection;
        }
    }
}

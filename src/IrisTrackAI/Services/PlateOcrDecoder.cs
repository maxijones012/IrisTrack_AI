using IrisTrackAI.Models;

namespace IrisTrackAI.Services;

/// <summary>CCT-XS v2 uses ten independent character slots, not CTC decoding.</summary>
public static class PlateOcrDecoder
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_";
    public const int Slots = 10;

    public static PlateReading Decode(ReadOnlySpan<float> probabilities)
    {
        if (probabilities.Length != Slots * Alphabet.Length)
            throw new InvalidOperationException("Salida de OCR incompatible con CCT-XS v2.");
        var chars = new char[Slots];
        var confidence = new float[Slots];
        for (var slot = 0; slot < Slots; slot++)
        {
            int best = 0;
            for (var c = 0; c < Alphabet.Length; c++)
            {
                var value = probabilities[slot * Alphabet.Length + c];
                if (!float.IsFinite(value) || value < 0 || value > 1.001f)
                    throw new InvalidOperationException("El lector devolvió probabilidades inválidas.");
                if (value > probabilities[slot * Alphabet.Length + best]) best = c;
            }
            chars[slot] = Alphabet[best];
            confidence[slot] = probabilities[slot * Alphabet.Length + best];
        }
        // Sólo se retira el relleno final. Nunca se corrigen O/0 ni letras repetidas.
        var text = new string(chars).TrimEnd('_');
        if (text.Length == 0) return new PlateReading("", 0, 0);
        var actual = confidence.Take(text.Length).ToArray();
        return new PlateReading(text, actual.Average(), actual.Min());
    }
}

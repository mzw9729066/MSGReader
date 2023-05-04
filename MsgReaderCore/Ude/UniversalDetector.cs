/* ***** BEGIN LICENSE BLOCK *****
 * Version: MPL 1.1/GPL 2.0/LGPL 2.1
 *
 * The contents of this file are subject to the Mozilla Public License Version
 * 1.1 (the "License"); you may not use this file except in compliance with
 * the License. You may obtain a copy of the License at
 * http://www.mozilla.org/MPL/
 *
 * Software distributed under the License is distributed on an "AS IS" basis,
 * WITHOUT WARRANTY OF ANY KIND, either express or implied. See the License
 * for the specific language governing rights and limitations under the
 * License.
 *
 * The Original Code is Mozilla Universal charset detector code.
 *
 * The Initial Developer of the Original Code is
 * Netscape Communications Corporation.
 * Portions created by the Initial Developer are Copyright (C) 2001
 * the Initial Developer. All Rights Reserved.
 *
 * Contributor(s):
 *          Kohei TAKETA <k-tak@void.in> (Java port)
 *          Rudi Pettazzi <rudi.pettazzi@gmail.com> (C# port)
 *
 * Refactoring to the code done by Kees van Spelde so that it works in this project
 * Copyright (c) 2023 Magic-Sessions. (www.magic-sessions.com)
 *
 * Alternatively, the contents of this file may be used under the terms of
 * either the GNU General Public License Version 2 or later (the "GPL"), or
 * the GNU Lesser General Public License Version 2.1 or later (the "LGPL"),
 * in which case the provisions of the GPL or the LGPL are applicable instead
 * of those above. If you wish to allow use of your version of this file only
 * under the terms of either the GPL or the LGPL, and not to allow others to
 * use your version of this file under the terms of the MPL, indicate your
 * decision by deleting the provisions above and replace them with the notice
 * and other provisions required by the GPL or the LGPL. If you do not delete
 * the provisions above, a recipient may use your version of this file under
 * the terms of any one of the MPL, the GPL or the LGPL.
 *
 * ***** END LICENSE BLOCK ***** */

namespace MsgReader.Ude;

#region Internal enum InputState
internal enum InputState
{
    PureAscii = 0,
    EscAscii = 1,
    HighByte = 2
}
#endregion

internal abstract class UniversalDetector
{
    protected const int FilterChineseSimplified = 1;
    protected const int FilterChineseTraditional = 2;
    protected const int FilterJapanese = 4;
    protected const int FilterKorean = 8;
    protected const int FilterNonCjk = 16;
    protected const int FilterAll = 31;

    protected static int FilterChinese =
        FilterChineseSimplified | FilterChineseTraditional;

    protected static int FilterCjk =
        FilterJapanese | FilterKorean | FilterChineseSimplified
        | FilterChineseTraditional;

    protected const float ShortcutThreshold = 0.95f;
    protected const float MinimumThreshold = 0.20f;

    internal InputState InputState;
    protected bool Start;
    protected bool GotData;
    protected bool Done;
    protected byte LastChar;
    protected int BestGuess;
    protected const int ProbersNum = 3;
    protected int LanguageFilter;
    protected CharsetProber[] CharsetProbers = new CharsetProber[ProbersNum];
    protected CharsetProber EscCharsetProber;
    protected string DetectedCharset;

    internal UniversalDetector(int languageFilter)
    {
        Start = true;
        InputState = InputState.PureAscii;
        LastChar = 0x00;
        BestGuess = -1;
        LanguageFilter = languageFilter;
    }

    public virtual void Feed(byte[] buf, int offset, int len)
    {
        if (Done) return;

        if (len > 0)
            GotData = true;

        // If the data starts with BOM, we know it is UTF
        if (Start)
        {
            Start = false;
            if (len > 3)
                switch (buf[0])
                {
                    case 0xEF:
                        if (0xBB == buf[1] && 0xBF == buf[2])
                            DetectedCharset = "UTF-8";
                        break;
                    case 0xFE:
                        if (0xFF == buf[1] && 0x00 == buf[2] && 0x00 == buf[3])
                            // FE FF 00 00  UCS-4, unusual octet order BOM (3412)
                            DetectedCharset = "X-ISO-10646-UCS-4-3412";
                        else if (0xFF == buf[1])
                            DetectedCharset = "UTF-16BE";
                        break;
                    case 0x00:
                        if (0x00 == buf[1] && 0xFE == buf[2] && 0xFF == buf[3])
                            DetectedCharset = "UTF-32BE";
                        else if (0x00 == buf[1] && 0xFF == buf[2] && 0xFE == buf[3])
                            // 00 00 FF FE  UCS-4, unusual octet order BOM (2143)
                            DetectedCharset = "X-ISO-10646-UCS-4-2143";
                        break;
                    case 0xFF:
                        if (0xFE == buf[1] && 0x00 == buf[2] && 0x00 == buf[3])
                            DetectedCharset = "UTF-32LE";
                        else if (0xFE == buf[1])
                            DetectedCharset = "UTF-16LE";
                        break;
                } // switch

            if (DetectedCharset != null)
            {
                Done = true;
                return;
            }
        }

        for (var i = 0; i < len; i++)
            // other than 0xa0, if every other character is ascii, the page is ascii
            if ((buf[i] & 0x80) != 0 && buf[i] != 0xA0)
            {
                // we got a non-ascii byte (high-byte)
                if (InputState != InputState.HighByte)
                {
                    InputState = InputState.HighByte;

                    // kill EscCharsetProber if it is active
                    if (EscCharsetProber != null) EscCharsetProber = null;

                    // start multibyte and singlebyte charset prober
                    CharsetProbers[0] ??= new MBCSGroupProber();
                    CharsetProbers[1] ??= new SBCSGroupProber();
                    CharsetProbers[2] ??= new Latin1Prober();
                }
            }
            else
            {
                if (InputState == InputState.PureAscii &&
                    (buf[i] == 0x1B || (buf[i] == 0x7B && LastChar == 0x7E)))
                    // found escape character or HZ "~{"
                    InputState = InputState.EscAscii;
                LastChar = buf[i];
            }

        var st = ProbingState.NotMe;

        switch (InputState)
        {
            case InputState.EscAscii:
                EscCharsetProber ??= new EscCharsetProber();
                st = EscCharsetProber.HandleData(buf, offset, len);
                if (st == ProbingState.FoundIt)
                {
                    Done = true;
                    DetectedCharset = EscCharsetProber.GetCharsetName();
                }

                break;
            case InputState.HighByte:
                for (var i = 0; i < ProbersNum; i++)
                    if (CharsetProbers[i] != null)
                    {
                        st = CharsetProbers[i].HandleData(buf, offset, len);
#if DEBUG
                        CharsetProbers[i].DumpStatus();
#endif
                        if (st == ProbingState.FoundIt)
                        {
                            Done = true;
                            DetectedCharset = CharsetProbers[i].GetCharsetName();
                            return;
                        }
                    }

                break;
        }
    }

    /// <summary>
    ///     Notify detector that no further data is available.
    /// </summary>
    public virtual void DataEnd()
    {
        if (!GotData)
            // we haven't got any data yet, return immediately 
            // caller program sometimes call DataEnd before anything has 
            // been sent to detector
            return;

        if (DetectedCharset != null)
        {
            Done = true;
            Report(DetectedCharset, 1.0f);
            return;
        }

        if (InputState == InputState.HighByte)
        {
            var maxProberConfidence = 0.0f;
            var maxProber = 0;
            for (var i = 0; i < ProbersNum; i++)
                if (CharsetProbers[i] != null)
                {
                    var proberConfidence = CharsetProbers[i].GetConfidence();
                    if (proberConfidence > maxProberConfidence)
                    {
                        maxProberConfidence = proberConfidence;
                        maxProber = i;
                    }
                }

            if (maxProberConfidence > MinimumThreshold)
                Report(CharsetProbers[maxProber].GetCharsetName(), maxProberConfidence);
        }
        else if (InputState == InputState.PureAscii)
        {
            Report("ASCII", 1.0f);
        }
    }

    #region Reset
    /// <summary>
    ///     Clear internal state of charset detector.
    ///     In the original interface this method is protected.
    /// </summary>
    public virtual void Reset()
    {
        Done = false;
        Start = true;
        DetectedCharset = null;
        GotData = false;
        BestGuess = -1;
        InputState = InputState.PureAscii;
        LastChar = 0x00;
        EscCharsetProber?.Reset();

        for (var i = 0; i < ProbersNum; i++)
            if (CharsetProbers[i] != null)
                CharsetProbers[i].Reset();
    }
    #endregion

    protected abstract void Report(string charset, float confidence);
}
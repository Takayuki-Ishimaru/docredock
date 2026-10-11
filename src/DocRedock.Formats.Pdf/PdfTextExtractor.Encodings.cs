using System.Globalization;
using System.Text;

namespace DocRedock.Formats.Pdf;

// What the character codes of a simple font stand for when no ToUnicode map says: its /Encoding, one of the
// base encodings of PDF 32000-1 Annex D (WinAnsi, MacRoman, Standard, MacExpert) with the glyph names a
// /Differences array puts on top of it. Reading every code as Latin-1 was right for the ASCII letters only:
// Windows punctuation (the curly quotes, the dashes, the euro sign) came out as C1 control characters, the
// accented letters of a Mac font as other letters, and a ligature or a Greek letter named in /Differences as
// its code. A glyph name becomes text through the table below - the names of the Latin character set, the
// Latin Extended-A letters, the Greek alphabet and the symbols mathematical fonts name - and the rules of
// the Adobe Glyph List: "uni00E9" and "u1D7D9" are the characters they spell, "f_f_i" is the letters of its
// parts and "a.sc" is the glyph "a" in another style. A name none of these covers is not guessed.
public static partial class PdfTextExtractor
{
    // The glyph names of a base encoding from code 0x20 up, in code order (".notdef" where it defines none).
    // WinAnsi is Windows-1252 with a bullet for the codes it leaves unused, a space for 0xA0 and a hyphen for
    // 0xAD (Annex D); MacRoman is Mac OS Roman as Quartz writes it, the euro sign at 0xDB included.
    private const string WinAnsiNames = """
        space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk plus
        comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal
        greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright
        asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar braceright
        asciitilde bullet Euro bullet quotesinglbase florin quotedblbase ellipsis dagger daggerdbl circumflex
        perthousand Scaron guilsinglleft OE bullet Zcaron bullet bullet quoteleft quoteright quotedblleft
        quotedblright bullet endash emdash tilde trademark scaron guilsinglright oe bullet zcaron Ydieresis space
        exclamdown cent sterling currency yen brokenbar section dieresis copyright ordfeminine guillemotleft
        logicalnot hyphen registered macron degree plusminus twosuperior threesuperior acute mu paragraph
        periodcentered cedilla onesuperior ordmasculine guillemotright onequarter onehalf threequarters
        questiondown Agrave Aacute Acircumflex Atilde Adieresis Aring AE Ccedilla Egrave Eacute Ecircumflex
        Edieresis Igrave Iacute Icircumflex Idieresis Eth Ntilde Ograve Oacute Ocircumflex Otilde Odieresis
        multiply Oslash Ugrave Uacute Ucircumflex Udieresis Yacute Thorn germandbls agrave aacute acircumflex
        atilde adieresis aring ae ccedilla egrave eacute ecircumflex edieresis igrave iacute icircumflex idieresis
        eth ntilde ograve oacute ocircumflex otilde odieresis divide oslash ugrave uacute ucircumflex udieresis
        yacute thorn ydieresis
        """;

    private const string MacRomanNames = """
        space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk plus
        comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal
        greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright
        asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar braceright
        asciitilde .notdef Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis aacute agrave acircumflex
        adieresis atilde aring ccedilla eacute egrave ecircumflex edieresis iacute igrave icircumflex idieresis
        ntilde oacute ograve ocircumflex odieresis otilde uacute ugrave ucircumflex udieresis dagger degree cent
        sterling section bullet paragraph germandbls registered copyright trademark acute dieresis notequal AE
        Oslash infinity plusminus lessequal greaterequal yen mu partialdiff summation product pi integral
        ordfeminine ordmasculine Omega ae oslash questiondown exclamdown logicalnot radical florin approxequal
        increment guillemotleft guillemotright ellipsis space Agrave Atilde Otilde OE oe endash emdash quotedblleft
        quotedblright quoteleft quoteright divide lozenge ydieresis Ydieresis fraction Euro guilsinglleft
        guilsinglright fi fl daggerdbl periodcentered quotesinglbase quotedblbase perthousand Acircumflex
        Ecircumflex Aacute Edieresis Egrave Iacute Icircumflex Idieresis Igrave Oacute Ocircumflex apple Ograve
        Uacute Ucircumflex Ugrave dotlessi circumflex tilde macron breve dotaccent ring cedilla hungarumlaut ogonek
        caron
        """;

    private const string StandardNames = """
        space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft parenright asterisk plus
        comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal
        greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright
        asciicircum underscore quoteleft a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar
        braceright asciitilde .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef exclamdown cent
        sterling fraction yen florin section currency quotesingle quotedblleft guillemotleft guilsinglleft
        guilsinglright fi fl .notdef endash dagger daggerdbl periodcentered .notdef paragraph bullet quotesinglbase
        quotedblbase quotedblright guillemotright ellipsis perthousand .notdef questiondown .notdef grave acute
        circumflex tilde macron breve dotaccent dieresis .notdef ring cedilla .notdef hungarumlaut ogonek caron
        emdash .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef AE .notdef ordfeminine .notdef .notdef .notdef .notdef Lslash Oslash OE
        ordmasculine .notdef .notdef .notdef .notdef .notdef ae .notdef .notdef .notdef dotlessi .notdef .notdef
        lslash oslash oe germandbls .notdef .notdef .notdef .notdef
        """;

    private const string MacExpertNames = """
        space exclamsmall Hungarumlautsmall centoldstyle dollaroldstyle dollarsuperior ampersandsmall Acutesmall
        parenleftsuperior parenrightsuperior twodotenleader onedotenleader comma hyphen period fraction
        zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle sevenoldstyle
        eightoldstyle nineoldstyle colon semicolon .notdef threequartersemdash .notdef questionsmall .notdef
        .notdef .notdef .notdef Ethsmall .notdef .notdef onequarter onehalf threequarters oneeighth threeeighths
        fiveeighths seveneighths onethird twothirds .notdef .notdef .notdef .notdef .notdef .notdef ff fi fl ffi
        ffl parenleftinferior .notdef parenrightinferior Circumflexsmall hypheninferior Gravesmall Asmall Bsmall
        Csmall Dsmall Esmall Fsmall Gsmall Hsmall Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall Psmall Qsmall
        Rsmall Ssmall Tsmall Usmall Vsmall Wsmall Xsmall Ysmall Zsmall colonmonetary onefitted rupiah Tildesmall
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef .notdef
        """;

    // Glyph name and the code points it stands for, in code point order. The Greek letters are the Greek letters:
    // "Delta" and "Omega" are U+0394 and U+03A9 (the Adobe list has the increment and the ohm sign there), and "mu"
    // is the micro sign, as in the Adobe list.
    private const string GlyphTable = """
        space=0020 exclam=0021 exclamsmall=0021 quotedbl=0022 numbersign=0023 dollar=0024 dollaroldstyle=0024
        dollarsuperior=0024 percent=0025 ampersand=0026 ampersandsmall=0026 quotesingle=0027 parenleft=0028
        parenright=0029 asterisk=002A plus=002B comma=002C hyphen=002D hypheninferior=002D period=002E slash=002F
        zero=0030 zerooldstyle=0030 one=0031 onefitted=0031 oneoldstyle=0031 two=0032 twooldstyle=0032 three=0033
        threeoldstyle=0033 four=0034 fouroldstyle=0034 five=0035 fiveoldstyle=0035 six=0036 sixoldstyle=0036
        seven=0037 sevenoldstyle=0037 eight=0038 eightoldstyle=0038 nine=0039 nineoldstyle=0039 colon=003A
        semicolon=003B less=003C equal=003D greater=003E question=003F questionsmall=003F at=0040 bracketleft=005B
        backslash=005C bracketright=005D asciicircum=005E underscore=005F Gravesmall=0060 grave=0060 Asmall=0061
        Bsmall=0062 Csmall=0063 Dsmall=0064 Esmall=0065 Fsmall=0066 ff=0066,0066 ffi=0066,0066,0069
        ffl=0066,0066,006C fi=0066,0069 fl=0066,006C Gsmall=0067 Hsmall=0068 Ismall=0069 Jsmall=006A Ksmall=006B
        Lsmall=006C Msmall=006D Nsmall=006E Osmall=006F Psmall=0070 Qsmall=0071 Rsmall=0072 Ssmall=0073 Tsmall=0074
        Usmall=0075 Vsmall=0076 Wsmall=0077 Xsmall=0078 Ysmall=0079 Zsmall=007A braceleft=007B bar=007C
        braceright=007D asciitilde=007E nbspace=00A0 nonbreakingspace=00A0 exclamdown=00A1 cent=00A2
        centoldstyle=00A2 sterling=00A3 currency=00A4 yen=00A5 brokenbar=00A6 section=00A7 dieresis=00A8
        copyright=00A9 copyrightserif=00A9 ordfeminine=00AA guillemotleft=00AB logicalnot=00AC sfthyphen=00AD
        registered=00AE registerserif=00AE macron=00AF degree=00B0 plusminus=00B1 twosuperior=00B2
        threesuperior=00B3 Acutesmall=00B4 acute=00B4 mu=00B5 mu1=00B5 paragraph=00B6 periodcentered=00B7
        cedilla=00B8 onesuperior=00B9 ordmasculine=00BA guillemotright=00BB onequarter=00BC onehalf=00BD
        threequarters=00BE questiondown=00BF Agrave=00C0 Aacute=00C1 Acircumflex=00C2 Atilde=00C3 Adieresis=00C4
        Aring=00C5 AE=00C6 Ccedilla=00C7 Egrave=00C8 Eacute=00C9 Ecircumflex=00CA Edieresis=00CB Igrave=00CC
        Iacute=00CD Icircumflex=00CE Idieresis=00CF Eth=00D0 Ntilde=00D1 Ograve=00D2 Oacute=00D3 Ocircumflex=00D4
        Otilde=00D5 Odieresis=00D6 multiply=00D7 Oslash=00D8 Ugrave=00D9 Uacute=00DA Ucircumflex=00DB
        Udieresis=00DC Yacute=00DD Thorn=00DE germandbls=00DF agrave=00E0 aacute=00E1 acircumflex=00E2 atilde=00E3
        adieresis=00E4 aring=00E5 ae=00E6 ccedilla=00E7 egrave=00E8 eacute=00E9 ecircumflex=00EA edieresis=00EB
        igrave=00EC iacute=00ED icircumflex=00EE idieresis=00EF Ethsmall=00F0 eth=00F0 ntilde=00F1 ograve=00F2
        oacute=00F3 ocircumflex=00F4 otilde=00F5 odieresis=00F6 divide=00F7 oslash=00F8 ugrave=00F9 uacute=00FA
        ucircumflex=00FB udieresis=00FC yacute=00FD thorn=00FE ydieresis=00FF Amacron=0100 amacron=0101 Abreve=0102
        abreve=0103 Aogonek=0104 aogonek=0105 Cacute=0106 cacute=0107 Ccircumflex=0108 ccircumflex=0109
        Cdotaccent=010A cdotaccent=010B Ccaron=010C ccaron=010D Dcaron=010E dcaron=010F Dcroat=0110 dcroat=0111
        Emacron=0112 emacron=0113 Ebreve=0114 ebreve=0115 Edotaccent=0116 edotaccent=0117 Eogonek=0118 eogonek=0119
        Ecaron=011A ecaron=011B Gcircumflex=011C gcircumflex=011D Gbreve=011E gbreve=011F Gdotaccent=0120
        gdotaccent=0121 Gcommaaccent=0122 gcommaaccent=0123 Hcircumflex=0124 hcircumflex=0125 Hbar=0126 hbar=0127
        Itilde=0128 itilde=0129 Imacron=012A imacron=012B Ibreve=012C ibreve=012D Iogonek=012E iogonek=012F
        Idotaccent=0130 dotlessi=0131 IJ=0132 ij=0133 Jcircumflex=0134 jcircumflex=0135 Kcommaaccent=0136
        kcommaaccent=0137 kgreenlandic=0138 Lacute=0139 lacute=013A Lcommaaccent=013B lcommaaccent=013C Lcaron=013D
        lcaron=013E Ldot=013F ldot=0140 Lslash=0141 lslash=0142 Nacute=0143 nacute=0144 Ncommaaccent=0145
        ncommaaccent=0146 Ncaron=0147 ncaron=0148 napostrophe=0149 Eng=014A eng=014B Omacron=014C omacron=014D
        Obreve=014E obreve=014F Ohungarumlaut=0150 ohungarumlaut=0151 OE=0152 oe=0153 Racute=0154 racute=0155
        Rcommaaccent=0156 rcommaaccent=0157 Rcaron=0158 rcaron=0159 Sacute=015A sacute=015B Scircumflex=015C
        scircumflex=015D Scedilla=015E scedilla=015F Scaron=0160 scaron=0161 Tcedilla=0162 Tcommaaccent=0162
        tcedilla=0163 tcommaaccent=0163 Tcaron=0164 tcaron=0165 Tbar=0166 tbar=0167 Utilde=0168 utilde=0169
        Umacron=016A umacron=016B Ubreve=016C ubreve=016D Uring=016E uring=016F Uhungarumlaut=0170
        uhungarumlaut=0171 Uogonek=0172 uogonek=0173 Wcircumflex=0174 wcircumflex=0175 Ycircumflex=0176
        ycircumflex=0177 Ydieresis=0178 Zacute=0179 zacute=017A Zdotaccent=017B zdotaccent=017C Zcaron=017D
        zcaron=017E longs=017F florin=0192 Scommaaccent=0218 scommaaccent=0219 dotlessj=0237 Circumflexsmall=02C6
        circumflex=02C6 caron=02C7 breve=02D8 dotaccent=02D9 ring=02DA ogonek=02DB Tildesmall=02DC tilde=02DC
        Hungarumlautsmall=02DD hungarumlaut=02DD Alpha=0391 Beta=0392 Gamma=0393 Delta=0394 Epsilon=0395 Zeta=0396
        Eta=0397 Theta=0398 Iota=0399 Kappa=039A Lambda=039B Mu=039C Nu=039D Xi=039E Omicron=039F Pi=03A0 Rho=03A1
        Sigma=03A3 Tau=03A4 Upsilon=03A5 Phi=03A6 Chi=03A7 Psi=03A8 Omega=03A9 alpha=03B1 beta=03B2 gamma=03B3
        delta=03B4 epsilon=03B5 zeta=03B6 eta=03B7 theta=03B8 iota=03B9 kappa=03BA lambda=03BB nu=03BD xi=03BE
        omicron=03BF pi=03C0 rho=03C1 sigma1=03C2 sigma=03C3 tau=03C4 upsilon=03C5 phi=03C6 chi=03C7 psi=03C8
        omega=03C9 theta1=03D1 Upsilon1=03D2 phi1=03D5 omega1=03D6 pi1=03D6 rho1=03F1 epsilon1=03F5 endash=2013
        emdash=2014 threequartersemdash=2014 quoteleft=2018 quoteright=2019 quotesinglbase=201A quotedblleft=201C
        quotedblright=201D quotedblbase=201E dagger=2020 daggerdbl=2021 bullet=2022 onedotenleader=2024
        twodotenleader=2025 ellipsis=2026 perthousand=2030 minute=2032 prime=2032 second=2033 guilsinglleft=2039
        guilsinglright=203A fraction=2044 parenleftsuperior=207D parenrightsuperior=207E parenleftinferior=208D
        parenrightinferior=208E colonmonetary=20A1 rupiah=20A8 Euro=20AC Ifraktur=2111 weierstrass=2118
        Rfraktur=211C trademark=2122 trademarkserif=2122 aleph=2135 onethird=2153 twothirds=2154 oneeighth=215B
        threeeighths=215C fiveeighths=215D seveneighths=215E arrowleft=2190 arrowup=2191 arrowright=2192
        arrowdown=2193 arrowboth=2194 arrownorthwest=2196 arrownortheast=2197 arrowsoutheast=2198
        arrowsouthwest=2199 mapsto=21A6 arrowhookleft=21A9 arrowhookright=21AA carriagereturn=21B5
        arrowdblleft=21D0 arrowdblup=21D1 arrowdblright=21D2 arrowdbldown=21D3 arrowdblboth=21D4 universal=2200
        partialdiff=2202 existential=2203 emptyset=2205 increment=2206 gradient=2207 nabla=2207 element=2208
        notelement=2209 owner=220B suchthat=220B product=220F coproduct=2210 summation=2211 minus=2212
        minusplus=2213 asteriskmath=2217 radical=221A proportional=221D infinity=221E angle=2220 bardbl=2225
        logicaland=2227 logicalor=2228 intersection=2229 union=222A integral=222B therefore=2234 similar=223C
        similarequal=2243 congruent=2245 approxequal=2248 notequal=2260 equivalence=2261 lessequal=2264
        greaterequal=2265 lessmuch=226A greatermuch=226B precedes=227A follows=227B propersubset=2282
        propersuperset=2283 notsubset=2284 reflexsubset=2286 reflexsuperset=2287 unionmulti=228E subsetsqequal=2291
        supersetsqequal=2292 intersectionsq=2293 unionsq=2294 circleplus=2295 circleminus=2296 circlemultiply=2297
        circledivide=2298 circledot=2299 turnstileleft=22A2 turnstileright=22A3 latticetop=22A4 perpendicular=22A5
        diamondmath=22C4 dotmath=22C5 star=22C6 ceilingleft=2308 ceilingright=2309 floorleft=230A floorright=230B
        angleleft=2329 angleright=232A triangle=25B3 triangleinv=25BD lozenge=25CA openbullet=25E6 spade=2660
        club=2663 heart=2665 diamond=2666 apple=F8FF
        """;

    private static readonly char[] NameSeparators = [' ', '\r', '\n'];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> GlyphText = new(() =>
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in GlyphTable.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = entry.IndexOf('=');
            var text = new StringBuilder();
            foreach (var codePoint in entry[(equals + 1)..].Split(','))
                text.Append(char.ConvertFromUtf32(int.Parse(codePoint, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            table[entry[..equals]] = text.ToString();
        }
        return table;
    });

    private static readonly Lazy<string?[]> WinAnsiCharacters = new(() => BaseCharacters(WinAnsiNames));
    private static readonly Lazy<string?[]> MacRomanCharacters = new(() => BaseCharacters(MacRomanNames));
    private static readonly Lazy<string?[]> StandardCharacters = new(() => BaseCharacters(StandardNames));
    private static readonly Lazy<string?[]> MacExpertCharacters = new(() => BaseCharacters(MacExpertNames));

    private static string?[] BaseCharacters(string names)
    {
        var characters = new string?[256];
        var list = names.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < list.Length; index++)
            characters[0x20 + index] = list[index] == ".notdef" ? null : GlyphToText(list[index]);
        return characters;
    }

    /// <summary>What the codes of a simple font stand for under its <c>/Encoding</c>:
    /// <see cref="Characters"/> holds the text of each code, null where the encoding defines none (such a code
    /// keeps the Latin-1 reading it always had), and <see cref="Unresolved"/> the codes the encoding gives a
    /// glyph name that is not known, so that what they stand for is not either.</summary>
    private sealed record PdfFontEncoding(string?[] Characters, IReadOnlySet<int> Unresolved);

    /// <summary>The encoding an <c>/Encoding</c> value stands for: a base encoding named, or a dictionary
    /// with a <c>/BaseEncoding</c> and a <c>/Differences</c> array (the value itself, or the object it
    /// refers to). Null when it defines no code, as a name the extractor does not know.</summary>
    private static PdfFontEncoding? ReadFontEncoding(string? value, IReadOnlyDictionary<int, string> objects)
    {
        if (ResolveValue(value, objects)?.TrimStart() is not { Length: > 0 } text) return null;
        string?[] baseCharacters;
        string? differences = null;
        if (text[0] == '/') baseCharacters = BaseEncoding(ReadName(text));
        else if (text.StartsWith("<<", StringComparison.Ordinal))
        {
            var entries = ReadDictionaryEntries(text);
            baseCharacters = BaseEncoding(ReadName(ResolveValue(EntryValue(entries, "BaseEncoding"), objects)));
            differences = ResolveValue(EntryValue(entries, "Differences"), objects);
        }
        else return null;
        var characters = (string?[])baseCharacters.Clone();
        var unresolved = new HashSet<int>();
        // MacExpert is only partly known here: a code of it not in the table is a letter or figure of an
        // expert set, never Latin-1.
        if (ReferenceEquals(baseCharacters, MacExpertCharacters.Value))
            for (var code = 0x20; code < characters.Length; code++)
                if (characters[code] is null) unresolved.Add(code);
        if (differences is not null) ApplyDifferences(differences, characters, unresolved);
        return characters.Any(character => character is not null) || unresolved.Count > 0 ? new PdfFontEncoding(characters, unresolved) : null;
    }

    private static string?[] BaseEncoding(string? name) => name switch
    {
        "WinAnsiEncoding" => WinAnsiCharacters.Value,
        "MacRomanEncoding" => MacRomanCharacters.Value,
        "StandardEncoding" => StandardCharacters.Value,
        "MacExpertEncoding" => MacExpertCharacters.Value,
        _ => new string?[256],
    };

    /// <summary>Applies a <c>/Differences</c> array: a number sets the code the names after it start at, and
    /// each name takes that code and the next.</summary>
    private static void ApplyDifferences(string array, string?[] characters, HashSet<int> unresolved)
    {
        var index = SkipSpace(array, 0);
        if (index >= array.Length || array[index] != '[') return;
        var code = 0;
        for (index++; ;)
        {
            index = SkipSpace(array, index);
            if (index >= array.Length || array[index] == ']') return;
            var end = array[index] == '/' ? NameEnd(array, index + 1) : NameEnd(array, index);
            if (end == index) { index++; continue; }
            if (array[index] == '/')
            {
                if (code is >= 0 and < 256)
                {
                    var text = GlyphToText(DecodeName(array[(index + 1)..end]));
                    characters[code] = text;
                    if (text is null) unresolved.Add(code);
                    else unresolved.Remove(code);
                }
                code++;
            }
            else if (ReadNumber(array[index..end]) is { } number && number >= 0 && number < 65_536 && number == Math.Floor(number))
                code = (int)number;
            index = end;
        }
    }

    /// <summary>The text a glyph name stands for, or null when it is not known: <c>.notdef</c> is nothing, and
    /// otherwise the name is read as the Adobe Glyph List does - the part before the first period, split at
    /// each underscore into components that are all known.</summary>
    private static string? GlyphToText(string name)
    {
        if (name == ".notdef") return string.Empty;
        var period = name.IndexOf('.');
        var glyph = period < 0 ? name : name[..period];
        if (glyph.Length == 0) return null;
        var text = new StringBuilder();
        foreach (var component in glyph.Split('_'))
        {
            if (ComponentText(component) is not { } characters) return null;
            text.Append(characters);
        }
        return text.ToString();
    }

    private static string? ComponentText(string name)
    {
        if (GlyphText.Value.TryGetValue(name, out var known)) return known;
        if (name.Length == 1 && char.IsAsciiLetter(name[0])) return name;
        // "uni" and groups of four hexadecimal digits spell UTF-16 code units; "u" and four to six digits one code point.
        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length > 3 && (name.Length - 3) % 4 == 0 && IsHexadecimal(name.AsSpan(3)))
        {
            var text = new StringBuilder();
            for (var at = 3; at < name.Length; at += 4)
                text.Append((char)int.Parse(name.AsSpan(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return text.ToString() is var result && !result.Any(char.IsSurrogate) ? result : null;
        }
        if (name.Length is >= 5 and <= 7 && name[0] == 'u' && IsHexadecimal(name.AsSpan(1)) &&
            int.Parse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) is var codePoint && Rune.IsValid(codePoint))
            return char.ConvertFromUtf32(codePoint);
        return null;
    }

    private static bool IsHexadecimal(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }
}

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Content.Shared._CMU14.Threats.Mobs.Abomination;
using Content.Shared._CMU14.Yautja;
using Content.Shared._RMC14.Chat;
using Content.Shared.AU14;
using Content.Shared._RMC14.Xenonids;
using Content.Shared._RMC14.Xenonids.Evolution;
using Content.Shared.Popups;
using Content.Shared.Radio;
using Content.Shared.Speech;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using Content.Shared._RMC14.Xenonids.Hive;
using CultistComponent = Content.Shared._CMU14.Threats.Mobs.Cultist.CultistComponent;

namespace Content.Shared.Chat;

public abstract partial class SharedChatSystem : EntitySystem
{
    public const char RadioCommonPrefix = ';';
    public const char RadioChannelPrefix = ':';
    public const char RadioChannelAltPrefix = '.';
    public const char LocalPrefix = '>';
    public const char ConsolePrefix = '/';
    public const char DeadPrefix = '\\';
    public const char LOOCPrefix = '(';
    public const char OOCPrefix = '[';
    public const char EmotesPrefix = '@';
    public const char EmotesAltPrefix = '*';
    public const char AdminPrefix = ']';
    public const char WhisperPrefix = ',';
    public const char MentorPrefix = '}';
    public const char DefaultChannelKey = 'h';

    public static readonly ProtoId<RadioChannelPrototype> CommonChannel = "MarineCommon";
    public static readonly ProtoId<RadioChannelPrototype> HivemindChannel = "Hivemind";

    public static readonly string DefaultChannelPrefix = $"{RadioChannelPrefix}{DefaultChannelKey}";
    public static readonly ProtoId<SpeechVerbPrototype> DefaultSpeechVerb = "Default";

    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private XenoEvolutionSystem _xenoEvolution = default!;
    [Dependency] private SharedXenoHiveSystem _hive = default!;

    // RMC14
    public FrozenDictionary<string, RadioChannelPrototype> _channelLookup = default!;
    public FrozenSet<char> _validPrefixes = default!;
    // RMC14

    public override void Initialize()
    {
        base.Initialize();
        DebugTools.Assert(_prototypeManager.HasIndex(CommonChannel));
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypeReload);
        CacheRadios();
    }

    protected virtual void OnPrototypeReload(PrototypesReloadedEventArgs obj)
    {
        if (obj.WasModified<RadioChannelPrototype>())
            CacheRadios();
    }

    private void CacheRadios()
    {
        // RMC14
        var channelDict = new Dictionary<string, RadioChannelPrototype>();
        var prefixSet = new HashSet<char>();

        foreach (var radioChannel in _prototypeManager.EnumeratePrototypes<RadioChannelPrototype>())
        {
            var keyCode = char.ToLowerInvariant(radioChannel.KeyCode);
            channelDict[$"{radioChannel.RadioPrefix}{keyCode}"] = radioChannel;

            prefixSet.Add(radioChannel.RadioPrefix);

            if (radioChannel.RadioPrefix == RadioChannelPrefix)
            {
                channelDict[$"{RadioChannelAltPrefix}{keyCode}"] = radioChannel;
                prefixSet.Add(RadioChannelAltPrefix);
            }
        }

        _channelLookup = channelDict.ToFrozenDictionary();
        _validPrefixes = prefixSet.ToFrozenSet();
        // RMC14
    }

    /// <summary>
    ///     Attempts to find an applicable <see cref="SpeechVerbPrototype"/> for a speaking entity's message.
    ///     If one is not found, returns <see cref="DefaultSpeechVerb"/>.
    /// </summary>
    public SpeechVerbPrototype GetSpeechVerb(EntityUid source, string message, SpeechComponent? speech = null)
    {
        if (!Resolve(source, ref speech, false))
            return _prototypeManager.Index(DefaultSpeechVerb);

        // check for a suffix-applicable speech verb
        SpeechVerbPrototype? current = null;
        foreach (var (str, id) in speech.SuffixSpeechVerbs)
        {
            var proto = _prototypeManager.Index(id);
            if (message.EndsWith(Loc.GetString(str)) && proto.Priority >= (current?.Priority ?? 0))
            {
                current = proto;
            }
        }

        // if no applicable suffix verb return the normal one used by the entity
        return current ?? _prototypeManager.Index(speech.SpeechVerb);
    }

    /// <summary>
    /// Splits the input message into a radio prefix part and the rest to preserve it during sanitization.
    /// </summary>
    /// <remarks>
    /// This is primarily for the chat emote sanitizer, which can match against ":b" as an emote, which is a valid radio keycode.
    /// </remarks>
    public void GetRadioKeycodePrefix(EntityUid source,
        string input,
        out string output,
        out string prefix)
    {
        prefix = string.Empty;
        output = input;

        // If the string is less than 2, then it's probably supposed to be an emote.
        // No one is sending empty radio messages!
        if (input.Length <= 2)
            return;

        // RMC14
        if (!_validPrefixes.Contains(input[0]))
            return;

        var lookupKey = $"{input[0]}{char.ToLowerInvariant(input[1])}";
        if (!_channelLookup.ContainsKey(lookupKey))
            return;
        // RMC14

        prefix = input[..2];
        output = input[2..];
    }

    /// <summary>
    ///     Attempts to resolve radio prefixes in chat messages (e.g., remove a leading ":e" and resolve the requested
    ///     channel. Returns true if a radio message was attempted, even if the channel is invalid.
    /// </summary>
    /// <param name="source">Source of the message</param>
    /// <param name="input">The message to be modified</param>
    /// <param name="output">The modified message</param>
    /// <param name="channel">The channel that was requested, if any</param>
    /// <param name="quiet">Whether or not to generate an informative pop-up message.</param>
    /// <returns></returns>
    public bool TryProccessRadioMessage(
        EntityUid source,
        string input,
        out string output,
        out RadioChannelPrototype? channel,
        bool quiet = false)
    {
        output = input.Trim();
        channel = null;

        if (input.Length == 0)
            return false;

        var hive = _hive.GetHive(source);
        // TODO RMC14 replace all of this with something else when chat code isnt a joke
        if (input.StartsWith(RadioCommonPrefix))
        {
            output = SanitizeMessageCapital(input[1..].TrimStart());
            channel = ((HasComp<XenoComponent>(source) && !IsHivebrokenXeno(source)) || HasComp<CultistComponent>(source))
                ? _prototypeManager.Index<RadioChannelPrototype>(HivemindChannel)
                : _prototypeManager.Index<RadioChannelPrototype>(CommonChannel);

            // RMC14
            if (channel?.ID == HivemindChannel.Id &&
                !CanUseHivemind(source, hive, quiet))
            {
                output = SanitizeMessageCapital(input[1..].TrimStart());
                return false;
            }
            // RMC14

            return true;
        }

        // RMC14
        if (!_validPrefixes.Contains(input[0]))
            return false;
        // RMC14

        if (input.Length < 2 || char.IsWhiteSpace(input[1]))
        {
            output = SanitizeMessageCapital(input[1..].TrimStart());
            if (HasComp<XenoComponent>(source) && !IsHivebrokenXeno(source))
            {
                Log.Info("Has XenoComponent, returning false");
                return false;
            }
            if (!quiet)
                _popup.PopupEntity(Loc.GetString("chat-manager-no-radio-key"), source, source);
            return true;
        }

        // RMC14
        var prefix = input[0];
        var channelKey = input[1];
        var lookupKey = $"{prefix}{char.ToLowerInvariant(channelKey)}";

        var foundChannel = _channelLookup.TryGetValue(lookupKey, out channel);

        output = SanitizeMessageCapital(input[2..].TrimStart());

        if (!foundChannel && char.ToLowerInvariant(channelKey) == DefaultChannelKey)
        {
            var ev = new GetDefaultRadioChannelEvent();
            RaiseLocalEvent(source, ev);

            if (ev.Channel == HivemindChannel.Id &&
                !CanUseHivemind(source, hive, quiet))
            {
                output = SanitizeMessageCapital(input[1..].TrimStart());
                return false;
            }

            if (ev.Channel != null)
                _prototypeManager.TryIndex(ev.Channel, out channel);

            return true;
        }

        if (!foundChannel && !quiet)
        {
            var msg = Loc.GetString("chat-manager-no-such-channel", ("key", channelKey));
            _popup.PopupEntity(msg, source, source);
        }

        var prefixEv = new ChatGetPrefixEvent(channel);
        RaiseLocalEvent(source, ref prefixEv);
        channel = prefixEv.Channel;

        if (channel?.ID == HivemindChannel.Id &&
            !CanUseHivemind(source, hive, quiet))
        {
            return false;
        }

        if (HasComp<XenoComponent>(source) && !IsHivebrokenXeno(source) && channel == null)
        {
            Log.Info("Has XenoComponent but no channel, returning false");
            return false;
        }
        // RMC14

        return true;
    }

    private bool CanUseHivemind(EntityUid source, Entity<HiveComponent>? hive, bool quiet)
    {
        if (_xenoEvolution.HasLiving<XenoEvolutionGranterComponent>(1, null, hive))
            return true;

        if (!quiet)
            _popup.PopupEntity(Loc.GetString("rmc-no-queen-hivemind-chat"), source, source, PopupType.LargeCaution);

        return false;
    }

    private bool IsHivebrokenXeno(EntityUid uid)
    {
        return HasComp<YautjaHivebrokenXenoComponent>(uid) ||
               TryComp(uid, out YautjaThrallComponent? thrall) && thrall.Hivebroken;
    }

    public string SanitizeMessageCapital(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;
        // Capitalize first letter
        message = OopsConcat(char.ToUpper(message[0]).ToString(), message.Remove(0, 1));
        return message;
    }

    private static string OopsConcat(string a, string b)
    {
        // This exists to prevent Roslyn being clever and compiling something that fails sandbox checks.
        return a + b;
    }

    public string SanitizeMessageCapitalizeTheWordI(string message, string theWordI = "i")
    {
        if (string.IsNullOrEmpty(message))
            return message;

        for
        (
            var index = message.IndexOf(theWordI);
            index != -1;
            index = message.IndexOf(theWordI, index + 1)
        )
        {
            // Stops the code If It's tryIng to capItalIze the letter I In the mIddle of words
            // Repeating the code twice is the simplest option
            if (index + 1 < message.Length && char.IsLetter(message[index + 1]))
                continue;
            if (index - 1 >= 0 && char.IsLetter(message[index - 1]))
                continue;

            var beforeTarget = message.Substring(0, index);
            var target = message.Substring(index, theWordI.Length);
            var afterTarget = message.Substring(index + theWordI.Length);

            message = beforeTarget + target.ToUpper() + afterTarget;
        }

        return message;
    }

    public static string SanitizeAnnouncement(string message, int maxLength = 0, int maxNewlines = 2)
    {
        var trimmed = message.Trim();
        if (maxLength > 0 && trimmed.Length > maxLength)
        {
            trimmed = $"{message[..maxLength]}...";
        }

        // No more than max newlines, other replaced to spaces
        if (maxNewlines > 0)
        {
            var chars = trimmed.ToCharArray();
            var newlines = 0;
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '\n')
                    continue;

                if (newlines >= maxNewlines)
                    chars[i] = ' ';

                newlines++;
            }

            return new string(chars);
        }

        return trimmed;
    }

    public static string InjectTagInsideTag(ChatMessage message, string outerTag, string innerTag, string? tagParameter)
    {
        var rawmsg = message.WrappedMessage;
        var tagStart = rawmsg.IndexOf($"[{outerTag}]");
        var tagEnd = rawmsg.IndexOf($"[/{outerTag}]");
        if (tagStart < 0 || tagEnd < 0) //If the outer tag is not found, the injection is not performed
            return rawmsg;
        tagStart += outerTag.Length + 2;

        string innerTagProcessed = tagParameter != null ? $"[{innerTag}={tagParameter}]" : $"[{innerTag}]";

        rawmsg = rawmsg.Insert(tagEnd, $"[/{innerTag}]");
        rawmsg = rawmsg.Insert(tagStart, innerTagProcessed);

        return rawmsg;
    }

    /// <summary>
    /// Injects a tag around all found instances of a specific string in a ChatMessage.
    /// Excludes strings inside other tags and brackets.
    /// </summary>
    public static string InjectTagAroundString(
        ChatMessage message,
        string targetString,
        string tag,
        string? tagParameter,
        bool targetIsRegex = false)
    {
        var rawmsg = message.WrappedMessage;
        var targetPattern = targetIsRegex ? targetString : Regex.Escape(targetString);
        var regex = new Regex("(?i)(" + targetPattern + ")(?-i)(?![^[]*])");
        rawmsg = regex.Replace(rawmsg, $"[{tag}={tagParameter}]$1[/{tag}]");
        return rawmsg;
    }

    public static string GetStringInsideTag(ChatMessage message, string tag)
    {
        var rawmsg = message.WrappedMessage;
        var tagStart = rawmsg.IndexOf($"[{tag}]");
        var tagEnd = rawmsg.IndexOf($"[/{tag}]");
        if (tagStart < 0 || tagEnd < 0)
            return "";
        tagStart += tag.Length + 2;
        return rawmsg.Substring(tagStart, tagEnd - tagStart);
    }

    // CMU: Per-word and per-phrase bold/italic markup system
    //
    // Phrase-level: "**phrase**" bolds, "//phrase//" italicizes, "***phrase***"
    // does both. Word-level: "word*" bolds, "word/" italicizes, "word***" does
    // both. Optional trailing punctuation (!?.,;:) directly before the marker is
    // included inside the emphasis, so "HELP!*" bolds "HELP!" as one unit.
    //
    // Regexes are applied most-specific-marker-first (triple, then double, then
    // single) so a longer marker sequence is never partially consumed by a
    // shorter pattern before it gets a chance to match as a whole.
    private static readonly Regex PhraseBoldItalicRegex = new(@"\*\*\*(.+?)\*\*\*", RegexOptions.Compiled);
    private static readonly Regex PhraseBoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex PhraseItalicRegex = new(@"//(.+?)//", RegexOptions.Compiled);
    private static readonly Regex InlineBoldItalicRegex = new(@"(\w+[!?.,;:]*)\*\*\*(?=\s|$)", RegexOptions.Compiled);
    private static readonly Regex InlineBoldRegex = new(@"(\w+[!?.,;:]*)\*(?=\s|$)", RegexOptions.Compiled);
    private static readonly Regex InlineItalicRegex = new(@"(\w+[!?.,;:]*)/(?=\s|$)", RegexOptions.Compiled);

    private const char BoldSentinelStart = '\uE000';
    private const char BoldSentinelEnd = '\uE001';
    private const char ItalicSentinelStart = '\uE002';
    private const char ItalicSentinelEnd = '\uE003';

    /// <summary>
    /// Applies phrase-level markup first (***, then **, then //), then falls
    /// back to word-level markup (word***, word*, word/) for anything left
    /// unmarked. Uses sentinel characters so the markup survives
    /// FormattedMessage.EscapeText. Call ResolveBoldSentinels after escaping to
    /// turn sentinels into real tags, or StripBoldSentinels if the destination
    /// doesn't support markup (e.g. hidden "X emotes..." popups).
    /// </summary>
    public string MarkInlineFormatting(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var result = PhraseBoldItalicRegex.Replace(message, m =>
            $"{BoldSentinelStart}{ItalicSentinelStart}{m.Groups[1].Value}{ItalicSentinelEnd}{BoldSentinelEnd}");

        result = PhraseBoldRegex.Replace(result, m =>
            $"{BoldSentinelStart}{m.Groups[1].Value}{BoldSentinelEnd}");

        result = PhraseItalicRegex.Replace(result, m =>
            $"{ItalicSentinelStart}{m.Groups[1].Value}{ItalicSentinelEnd}");

        result = InlineBoldItalicRegex.Replace(result, m =>
            $"{BoldSentinelStart}{ItalicSentinelStart}{m.Groups[1].Value}{ItalicSentinelEnd}{BoldSentinelEnd}");

        result = InlineBoldRegex.Replace(result, m =>
            $"{BoldSentinelStart}{m.Groups[1].Value}{BoldSentinelEnd}");

        result = InlineItalicRegex.Replace(result, m =>
            $"{ItalicSentinelStart}{m.Groups[1].Value}{ItalicSentinelEnd}");

        return result;
    }

    public string ResolveBoldSentinels(string escapedMessage)
    {
        // CMU: RobustToolbox's rich text tags each push their own font onto
        // a stack independently (see BoldTag.cs / ItalicTag.cs / BoldItalicTag.cs
        // in the engine) - nesting [bold][italic]...[/italic][/bold] does NOT
        // combine into a bold-italic font, the inner tag's font just overwrites
        // the outer one and bold is lost. [bolditalic]...[/bolditalic] is a
        // separate dedicated tag/font for the combined case. Collapse the
        // adjacent combined-sentinel pairs into that tag first, before
        // resolving whatever single-flag sentinels are left over.
        var result = escapedMessage
            .Replace($"{BoldSentinelStart}{ItalicSentinelStart}", "[bolditalic]")
            .Replace($"{ItalicSentinelEnd}{BoldSentinelEnd}", "[/bolditalic]");

        return result
            .Replace(BoldSentinelStart.ToString(), "[bold]")
            .Replace(BoldSentinelEnd.ToString(), "[/bold]")
            .Replace(ItalicSentinelStart.ToString(), "[italic]")
            .Replace(ItalicSentinelEnd.ToString(), "[/italic]");
    }

    /// <summary>
    /// Removes sentinel characters entirely without converting them to markup.
    /// Use this for destinations that don't render markup, such as the hidden
    /// "X emotes..." popup shown when a listener can't understand the language.
    /// </summary>
    public string StripBoldSentinels(string message)
    {
        return message
            .Replace(BoldSentinelStart.ToString(), "")
            .Replace(BoldSentinelEnd.ToString(), "")
            .Replace(ItalicSentinelStart.ToString(), "")
            .Replace(ItalicSentinelEnd.ToString(), "");
    }
}

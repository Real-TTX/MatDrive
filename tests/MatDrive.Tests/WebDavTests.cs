using System.Text;
using System.Xml;
using System.Xml.Linq;
using MatDrive.Services.WebDav;

namespace MatDrive.Tests;

/// <summary>
/// The parts of the WebDAV server that need no database: paths and hrefs, the headers Destination and If, the lock manager, the XML of the bodies and answers,
/// the Basic credentials and the names of the places above the files.
/// </summary>
public sealed class WebDavTests
{
    private static readonly XNamespace D = DavXml.Dav;
    private static readonly XNamespace Z = DavXml.Ms;
    private static readonly XNamespace X = "urn:example:custom";
    private static readonly DateTime Saturday = new(2026, 11, 7, 18, 7, 0, DateTimeKind.Utc);

    // ---- paths and hrefs --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/dav", "")]
    [InlineData("/dav/", "")]
    [InlineData("/DAV/My%20Drive/", "My Drive")]
    [InlineData("/dav/My%20Drive/Reports/a.txt", "My Drive|Reports|a.txt")]
    [InlineData("/dav//My%20Drive///a.txt", "My Drive|a.txt")]
    [InlineData("/dav/My%20Drive/a.txt?x=1#y", "My Drive|a.txt")]
    [InlineData("/dav/My%20Drive/%C3%9Cbersicht%20%F0%9F%98%80.txt", "My Drive|Übersicht 😀.txt")]
    [InlineData("/dav/My%20Drive/100%25%20%2B%20%23%26.txt", "My Drive|100% + #&.txt")]
    public void A_path_is_parsed_one_segment_at_a_time(string path, string expected)
        => Assert.Equal(expected, string.Join('|', DavPath.Parse(path)!));

    [Theory]
    [InlineData("/files/1")]
    [InlineData("/davx/a")]
    [InlineData("/dav/My%20Drive/..")]
    [InlineData("/dav/My%20Drive/%2E%2E/x")]
    [InlineData("/dav/My%20Drive/./a")]
    [InlineData("/dav/My%20Drive/a%2Fb.txt")]
    [InlineData("/dav/My%20Drive/a%5Cb.txt")]
    [InlineData("/dav/My%20Drive/a%00b.txt")]
    [InlineData("dav/My%20Drive")]
    [InlineData("")]
    public void A_path_outside_dav_or_with_a_forbidden_segment_is_refused(string path) => Assert.Null(DavPath.Parse(path));

    [Fact]
    public void A_path_deeper_than_the_limit_is_refused()
    {
        var deep = "/dav" + string.Concat(Enumerable.Repeat("/a", DavPath.MaxDepth));
        Assert.Equal(DavPath.MaxDepth, DavPath.Parse(deep)!.Count);
        Assert.Null(DavPath.Parse(deep + "/a"));
    }

    [Fact]
    public void The_base_path_of_the_app_comes_before_dav()
    {
        Assert.Equal(new[] { "My Drive" }, DavPath.Parse("/drive/dav/My%20Drive", "/drive")!);
        Assert.Null(DavPath.Parse("/dav/My%20Drive", "/drive"));
        Assert.Equal("/drive/dav/My%20Drive/", DavPath.Href("/drive", ["My Drive"], collection: true));
    }

    [Fact]
    public void An_href_is_escaped_per_segment_and_a_collection_ends_with_a_slash()
    {
        Assert.Equal("/dav/", DavPath.Href(string.Empty, [], collection: true));
        Assert.Equal("/dav/My%20Drive/", DavPath.Href(string.Empty, ["My Drive"], collection: true));
        Assert.Equal("/dav/My%20Drive/%C3%9Cbersicht%20%F0%9F%98%80.txt", DavPath.Href(string.Empty, ["My Drive", "Übersicht 😀.txt"], collection: false));
    }

    [Theory]
    [InlineData("Report 2026.docx")]
    [InlineData("Übersicht – Ärger.txt")]
    [InlineData("😀 party 🎉.png")]
    [InlineData("100% #1 & [draft] {x} + y=z; a,b @c $d 'e' (f) ~g!.txt")]
    [InlineData("日本語のファイル.pdf")]
    public void A_name_round_trips_through_an_href(string name)
    {
        var href = DavPath.Href(string.Empty, ["My Drive", name], collection: false);
        Assert.DoesNotContain(' ', href);
        Assert.Equal(new[] { "My Drive", name }, DavPath.Parse(href)!);
        Assert.Equal(new[] { "My Drive", name }, DavPath.ParseUrl("https://drive.example.com" + href, null, out _)!);
    }

    [Fact]
    public void Names_are_compared_in_nfc_without_regard_to_case()
    {
        const string decomposed = "U\u0308bersicht.txt"; // how the Finder sends Ü
        Assert.Equal("Übersicht.txt", DavPath.Nfc(decomposed));
        Assert.Equal(DavPath.Key(["My Drive", "übersicht.TXT"]), DavPath.Key(["MY DRIVE", decomposed]));
        Assert.True(DavStore.Same(decomposed, "ÜBERSICHT.txt"));
        Assert.False(DavStore.Same("a.txt", "b.txt"));
    }

    [Fact]
    public void Text_that_xml_cannot_carry_is_replaced()
    {
        Assert.Equal("a\uFFFDb", DavText.Clean("a\u0001b"));
        Assert.Equal("a\uFFFDb", DavText.Clean("a\uD800b"));
        Assert.Equal("😀 ok\t", DavText.Clean("😀 ok\t"));
    }

    // ---- Destination ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://localhost:4841/dav/My%20Drive/b.txt", "My Drive|b.txt")]
    [InlineData("https://another-name.example.com/dav/My%20Drive/b.txt", "My Drive|b.txt")] // the host is not compared (reverse proxy)
    [InlineData("/dav/My%20Drive/b.txt", "My Drive|b.txt")]
    [InlineData("  /dav/My%20Drive/Folder/  ", "My Drive|Folder")]
    [InlineData("http://localhost/dav/My Drive/with space.txt", "My Drive|with space.txt")]
    [InlineData("http://localhost/dav/Shared%20drives/Team/%C3%A4.txt", "Shared drives|Team|ä.txt")]
    [InlineData("http://localhost/dav/My%20Drive/Old/%2E%2E/b.txt", "My Drive|b.txt")] // a URL is normalised like every URI (RFC 3986, 5.2.4)
    public void The_destination_is_an_absolute_url_or_path(string value, string expected)
    {
        Assert.Equal(expected, string.Join('|', DavPath.ParseUrl(value, null, out var foreign)!));
        Assert.False(foreign);
    }

    [Theory]
    [InlineData("http://localhost/files/12", true)]
    [InlineData("/Account/Login", true)]
    [InlineData("/dav/My%20Drive/../x", false)]
    [InlineData("/dav/My%20Drive/%2E%2E/x", false)]
    [InlineData("http://localhost/dav/%2E%2E/files/12", true)] // the normalised URL is no longer below /dav
    [InlineData("ftp://localhost/dav/x", false)]
    [InlineData("My%20Drive/b.txt", false)]
    [InlineData("//elsewhere.example.com/dav/x", false)]
    [InlineData("", false)]
    public void A_destination_outside_dav_or_malformed_is_refused(string value, bool foreign)
    {
        Assert.Null(DavPath.ParseUrl(value, null, out var isForeign));
        Assert.Equal(foreign, isForeign);
    }

    // ---- If ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_if_header_has_lists_of_tokens_and_entity_tags()
    {
        var lists = DavIfHeader.Parse("(<opaquelocktoken:a-1> [\"e1\"]) (Not <DAV:no-lock>) <http://localhost/dav/My%20Drive/b.txt> ([W/\"e2\"] <opaquelocktoken:b-2>)")!;
        Assert.Equal(3, lists.Count);
        Assert.Null(lists[0].Resource);
        Assert.Equal(new DavCondition(false, "opaquelocktoken:a-1", null), lists[0].Conditions[0]);
        Assert.Equal(new DavCondition(false, null, "\"e1\""), lists[0].Conditions[1]);
        Assert.Null(lists[1].Resource);
        Assert.Equal(new DavCondition(true, DavIfHeader.NoLock, null), lists[1].Conditions.Single());
        Assert.Equal("http://localhost/dav/My%20Drive/b.txt", lists[2].Resource);
        Assert.Equal(new DavCondition(false, null, "W/\"e2\""), lists[2].Conditions[0]);
        Assert.Equal(new[] { "opaquelocktoken:a-1", "opaquelocktoken:b-2" }, DavIfHeader.Tokens(lists).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("<opaquelocktoken:a>")]
    [InlineData("(<opaquelocktoken:a>")]
    [InlineData("()")]
    [InlineData("(opaquelocktoken:a)")]
    [InlineData("([\"e1\")")]
    [InlineData("<http://localhost/dav/a>")]
    [InlineData("(<a>) garbage")]
    public void A_malformed_if_header_is_refused(string header) => Assert.Null(DavIfHeader.Parse(header));

    [Fact]
    public void No_if_header_holds()
    {
        Assert.Empty(DavIfHeader.Parse(null)!);
        Assert.Empty(DavIfHeader.Parse("  ")!);
        Assert.True(DavIfHeader.Evaluate([], _ => DavIfState.None, _ => true));
    }

    [Fact]
    public void The_if_header_holds_when_every_condition_of_one_list_holds()
    {
        var state = new DavIfState("\"abc\"", ["opaquelocktoken:mine"]);
        bool Holds(string header) => DavIfHeader.Evaluate(DavIfHeader.Parse(header)!, _ => state, token => token is "opaquelocktoken:mine" or "opaquelocktoken:other");

        Assert.True(Holds("(<opaquelocktoken:mine>)"));
        Assert.True(Holds("([\"abc\"])"));
        Assert.True(Holds("([W/\"abc\"] <opaquelocktoken:mine>)"));
        Assert.False(Holds("([\"xyz\"])"));
        Assert.False(Holds("(<opaquelocktoken:other>)")); // a lock of another resource
        Assert.True(Holds("(<opaquelocktoken:other>) (<opaquelocktoken:mine>)"));
        Assert.False(Holds("(<opaquelocktoken:mine> [\"xyz\"])"));
        Assert.True(Holds("(Not <DAV:no-lock>)"));
        Assert.False(Holds("(<DAV:no-lock>)"));
        Assert.False(Holds("(Not <opaquelocktoken:mine>)"));
    }

    [Fact]
    public void A_token_the_server_forgot_holds_while_nobody_else_locked_the_resource()
    {
        var lists = DavIfHeader.Parse("(<opaquelocktoken:from-before-a-restart>)")!;
        Assert.True(DavIfHeader.Evaluate(lists, _ => DavIfState.None, _ => false));
        Assert.False(DavIfHeader.Evaluate(lists, _ => new DavIfState(null, ["opaquelocktoken:somebody-else"]), _ => false));
    }

    [Fact]
    public void A_tagged_list_is_evaluated_against_its_own_resource()
    {
        var lists = DavIfHeader.Parse("<http://localhost/dav/My%20Drive/a.txt> ([\"a\"])")!;
        Assert.True(DavIfHeader.Evaluate(lists, r => new DavIfState(r is null ? "\"b\"" : "\"a\"", []), _ => false));
        Assert.False(DavIfHeader.Evaluate(lists, r => new DavIfState(r is null ? "\"a\"" : "\"b\"", []), _ => false));
    }

    [Theory]
    [InlineData("\"abc\"", "abc", true)]
    [InlineData("W/\"abc\"", "\"abc\"", true)]
    [InlineData("\"abc\"", "\"abd\"", false)]
    public void Entity_tags_are_compared_without_quotes_and_weakness(string a, string b, bool same) => Assert.Equal(same, DavIfHeader.SameTag(a, b));

    // ---- locks ------------------------------------------------------------------------------------------------------------------

    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    [Fact]
    public void An_exclusive_lock_keeps_every_other_lock_out()
    {
        var (locks, _) = NewLocks();
        var (outcome, made, _) = locks.Lock(1, [10, 11], exclusive: true, deep: false, "/dav/My%20Drive/a.txt", null, Five);
        Assert.Equal(DavLockOutcome.Locked, outcome);
        Assert.StartsWith(DavLockManager.TokenPrefix, made!.Token);
        Assert.True(locks.IsKnown(made.Token));

        Assert.Equal(DavLockOutcome.Conflict, locks.Lock(1, [10, 11], true, false, "a", null, Five).Outcome);
        var (shared, _, conflict) = locks.Lock(2, [10, 11], false, false, "a", null, Five);
        Assert.Equal(DavLockOutcome.Conflict, shared);
        Assert.Equal(made.Token, conflict!.Token);
        Assert.Equal(DavLockOutcome.Locked, locks.Lock(2, [10, 12], true, false, "b", null, Five).Outcome); // another file
    }

    [Fact]
    public void Shared_locks_live_side_by_side()
    {
        var (locks, _) = NewLocks();
        Assert.Equal(DavLockOutcome.Locked, locks.Lock(1, [10], false, false, "a", null, Five).Outcome);
        Assert.Equal(DavLockOutcome.Locked, locks.Lock(2, [10], false, false, "a", null, Five).Outcome);
        Assert.Equal(DavLockOutcome.Conflict, locks.Lock(3, [10], true, false, "a", null, Five).Outcome);
        Assert.Equal(2, locks.Covering([10]).Count);
    }

    [Fact]
    public void A_deep_lock_covers_everything_below_its_folder()
    {
        var (locks, _) = NewLocks();
        var folder = locks.Lock(1, [10, 20], true, deep: true, "f", null, Five).Lock!;
        Assert.Contains(folder, locks.Covering([10, 20, 30, 40]));
        Assert.Empty(locks.Covering([10, 21]));
        Assert.Equal(DavLockOutcome.Conflict, locks.Lock(2, [10, 20, 30], false, false, "g", null, Five).Outcome);
        Assert.NotNull(locks.FindConflict([10, 20, 0], exclusive: false, deep: false)); // a new name in the folder

        var (shallow, _) = NewLocks();
        shallow.Lock(1, [10, 20], true, deep: false, "f", null, Five);
        Assert.Empty(shallow.Covering([10, 20, 30]));
        Assert.Null(shallow.FindConflict([10, 20, 0], exclusive: true, deep: false));
    }

    [Fact]
    public void A_deep_lock_is_refused_while_something_below_is_locked_exclusively()
    {
        var (locks, _) = NewLocks();
        var below = locks.Lock(1, [10, 20, 30], false, false, "a", null, Five).Lock!;
        var (outcome, _, conflict) = locks.Lock(1, [10, 20], true, deep: true, "f", null, Five);
        Assert.Equal(DavLockOutcome.Conflict, outcome);
        Assert.Equal(below.Token, conflict!.Token);
        Assert.Equal(DavLockOutcome.Locked, locks.Lock(2, [10, 20], false, deep: true, "f", null, Five).Outcome); // shared over shared
        Assert.Single(locks.Below([10, 20]));
    }

    [Fact]
    public void A_lock_expires_and_a_refresh_extends_it()
    {
        var (locks, time) = NewLocks();
        var made = locks.Lock(1, [10], true, false, "a", null, Five).Lock!;
        time.Now += TimeSpan.FromMinutes(4);
        var refreshed = locks.Refresh(made.Token, 1, [10], Five);
        Assert.NotNull(refreshed);
        Assert.Equal(time.Now + Five, refreshed.Expires);
        Assert.Null(locks.Refresh(made.Token, 2, [10], Five)); // not a lock of this person
        Assert.Null(locks.Refresh(made.Token, 1, [11], Five)); // not a lock of this resource

        time.Now += TimeSpan.FromMinutes(4);
        Assert.True(locks.IsKnown(made.Token));
        time.Now += TimeSpan.FromMinutes(2);
        Assert.False(locks.IsKnown(made.Token));
        Assert.Empty(locks.Covering([10]));
        Assert.Equal(DavLockOutcome.Locked, locks.Lock(2, [10], true, false, "a", null, Five).Outcome);
    }

    [Fact]
    public void Only_the_holder_ends_a_lock_and_only_on_its_resource()
    {
        var (locks, _) = NewLocks();
        var made = locks.Lock(1, [10, 11], true, false, "a", null, Five).Lock!;
        Assert.Equal(DavUnlockOutcome.NotFound, locks.Unlock("opaquelocktoken:nothing", 1, [10, 11]));
        Assert.Equal(DavUnlockOutcome.Forbidden, locks.Unlock(made.Token, 2, [10, 11]));
        Assert.Equal(DavUnlockOutcome.NotCovered, locks.Unlock(made.Token, 1, [10, 12]));
        Assert.Equal(DavUnlockOutcome.Unlocked, locks.Unlock(made.Token, 1, [10, 11]));
        Assert.False(locks.IsKnown(made.Token));
    }

    [Fact]
    public void Moving_or_deleting_ends_the_locks_of_a_subtree()
    {
        var (locks, _) = NewLocks();
        var folder = locks.Lock(1, [10, 20], false, false, "f", null, Five).Lock!;
        var file = locks.Lock(1, [10, 20, 30], false, false, "a", null, Five).Lock!;
        var other = locks.Lock(1, [10, 21], false, false, "b", null, Five).Lock!;
        locks.RemoveSubtree([10, 20]);
        Assert.False(locks.IsKnown(folder.Token));
        Assert.False(locks.IsKnown(file.Token));
        Assert.True(locks.IsKnown(other.Token));
    }

    [Fact]
    public void A_change_needs_a_token_of_the_holder_for_every_locked_node()
    {
        var (locks, _) = NewLocks();
        var mine = locks.Lock(1, [10], true, false, "a", null, Five).Lock!;
        var group = locks.Covering([10]);
        Assert.Same(mine, DavLockManager.Unsubmitted([group], new HashSet<string>(), 1));
        Assert.Null(DavLockManager.Unsubmitted([group], new HashSet<string> { mine.Token }, 1));
        Assert.Same(mine, DavLockManager.Unsubmitted([group], new HashSet<string> { mine.Token }, 2)); // somebody else who knows the token
        Assert.Null(DavLockManager.Unsubmitted([[], []], new HashSet<string>(), 1));
    }

    [Fact]
    public void A_person_holds_a_bounded_number_of_locks()
    {
        var (locks, _) = NewLocks();
        for (var i = 1; i <= DavLockManager.MaxLocksPerUser; i++)
        {
            Assert.Equal(DavLockOutcome.Locked, locks.Lock(1, [i], true, false, "a", null, Five).Outcome);
        }

        Assert.Equal(DavLockOutcome.TooMany, locks.Lock(1, [100_000], true, false, "a", null, Five).Outcome);
        Assert.Equal(DavLockOutcome.Locked, locks.Lock(2, [100_000], true, false, "a", null, Five).Outcome);
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData("", 600)]
    [InlineData("Second-120", 120)]
    [InlineData("second-0", 1)]
    [InlineData("Second-99999999999", 3600)]
    [InlineData("Infinite, Second-4100000000", 3600)]
    [InlineData("Second-abc, Second-30", 30)]
    [InlineData("whatever", 600)]
    public void The_timeout_of_a_lock_is_bounded(string? header, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), DavLockManager.ParseTimeout(header));

    // ---- XML --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_propfind_body_says_what_it_asks_for()
    {
        Assert.Same(DavPropFind.AllProp, DavXml.PropFind(null));

        var prop = DavXml.PropFind(XDocument.Parse("<d:propfind xmlns:d=\"DAV:\" xmlns:z=\"urn:schemas-microsoft-com:\"><d:prop><d:getetag/><z:Win32FileAttributes/><d:getetag/></d:prop></d:propfind>"))!;
        Assert.Equal(DavPropMode.Prop, prop.Mode);
        Assert.Equal(new[] { D + "getetag", Z + "Win32FileAttributes" }, prop.Names);

        Assert.Equal(DavPropMode.PropName, DavXml.PropFind(XDocument.Parse("<propfind xmlns=\"DAV:\"><propname/></propfind>"))!.Mode);

        var all = DavXml.PropFind(XDocument.Parse("<propfind xmlns=\"DAV:\"><allprop/><include><quota-used-bytes/></include></propfind>"))!;
        Assert.Equal(DavPropMode.AllProp, all.Mode);
        Assert.Equal(new[] { DavProperties.QuotaUsed }, all.Names);

        Assert.Null(DavXml.PropFind(XDocument.Parse("<propfind><prop/></propfind>"))); // not in the namespace DAV:
    }

    [Fact]
    public void A_body_with_a_document_type_is_refused()
    {
        var body = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><propfind xmlns=\"DAV:\">&x;</propfind>");
        Assert.Throws<XmlException>(() => DavXml.Read(body));
        Assert.Throws<XmlException>(() => DavXml.Read(Encoding.UTF8.GetBytes("<propfind xmlns=\"DAV:\">")));
        Assert.Null(DavXml.Read(Encoding.UTF8.GetBytes(" \r\n ")));
        Assert.Null(DavXml.Read(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void A_proppatch_names_the_properties_it_sets_and_removes()
    {
        var doc = XDocument.Parse("""
            <D:propertyupdate xmlns:D="DAV:" xmlns:Z="urn:schemas-microsoft-com:">
              <D:set><D:prop><Z:Win32CreationTime>Sat, 07 Nov 2026 18:00:00 GMT</Z:Win32CreationTime><Z:Win32FileAttributes>00000020</Z:Win32FileAttributes></D:prop></D:set>
              <D:remove><D:prop><Z:Win32LastAccessTime/></D:prop></D:remove>
            </D:propertyupdate>
            """);
        Assert.Equal(new[] { Z + "Win32CreationTime", Z + "Win32FileAttributes", Z + "Win32LastAccessTime" }, DavXml.PropPatch(doc)!);
        Assert.Null(DavXml.PropPatch(XDocument.Parse("<propfind xmlns=\"DAV:\"/>")));
        Assert.Null(DavXml.PropPatch(null));
    }

    [Fact]
    public void A_lock_body_has_a_scope_a_type_and_an_owner()
    {
        var info = DavXml.LockInfo(XDocument.Parse(
            "<D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner><D:href>mailto:ada@example.com</D:href></D:owner></D:lockinfo>"))!;
        Assert.True(info.Exclusive);
        Assert.Equal("mailto:ada@example.com", info.Owner!.Element(D + "href")!.Value);

        Assert.False(DavXml.LockInfo(XDocument.Parse("<lockinfo xmlns=\"DAV:\"><lockscope><shared/></lockscope><locktype><write/></locktype></lockinfo>"))!.Exclusive);
        Assert.Null(DavXml.LockInfo(XDocument.Parse("<lockinfo xmlns=\"DAV:\"><lockscope><shared/><exclusive/></lockscope><locktype><write/></locktype></lockinfo>")));
        Assert.Null(DavXml.LockInfo(XDocument.Parse("<lockinfo xmlns=\"DAV:\"><lockscope><exclusive/></lockscope></lockinfo>")));

        var huge = DavXml.LockInfo(XDocument.Parse(
            $"<lockinfo xmlns=\"DAV:\"><lockscope><exclusive/></lockscope><locktype><write/></locktype><owner>{new string('x', DavXml.MaxOwner)}</owner></lockinfo>"))!;
        Assert.Null(huge.Owner); // the lock is kept, its owner is not
    }

    [Fact]
    public void Dates_are_written_as_webdav_wants_them()
    {
        Assert.Equal("Sat, 07 Nov 2026 18:07:00 GMT", DavXml.Rfc1123(Saturday));
        Assert.Equal("2026-11-07T18:07:00Z", DavXml.Iso(Saturday));
    }

    [Fact]
    public async Task A_multistatus_has_one_response_per_item_with_the_properties_asked_for()
    {
        var find = DavXml.PropFind(XDocument.Parse("""
            <d:propfind xmlns:d="DAV:" xmlns:z="urn:schemas-microsoft-com:" xmlns:x="urn:example:custom"><d:prop>
              <d:displayname/><d:resourcetype/><d:getcontentlength/><d:getetag/><d:getcontenttype/><d:getlastmodified/><d:creationdate/>
              <d:quota-available-bytes/><d:quota-used-bytes/><d:isreadonly/><z:Win32FileAttributes/><x:color/>
            </d:prop></d:propfind>
            """))!;
        var folder = new DavItem("Reports", true, 0, null, null, Saturday, Saturday, NodeId: 5, NodePath: "/5/");
        var file = new DavItem(".Übersicht 😀.txt", false, 1234, "text/plain", "abc123", Saturday, Saturday, NodeId: 6, NodePath: "/5/6/", ReadOnly: true);

        var doc = await RenderAsync(find, ("/dav/My%20Drive/Reports/", new DavPropContext(folder, [], new DavQuota(100, 900), DateTimeOffset.UtcNow)),
            ("/dav/My%20Drive/Reports/" + DavPath.Escape(file.Name), new DavPropContext(file, [], null, DateTimeOffset.UtcNow)));

        Assert.Equal(D + "multistatus", doc.Root!.Name);
        var responses = doc.Root.Elements(D + "response").ToList();
        Assert.Equal(2, responses.Count);

        var f = responses[0];
        Assert.Equal("/dav/My%20Drive/Reports/", f.Element(D + "href")!.Value);
        var found = Props(f, 200);
        Assert.NotNull(found.Element(D + "resourcetype")!.Element(D + "collection"));
        Assert.Equal("Reports", found.Element(D + "displayname")!.Value);
        Assert.Equal("900", found.Element(DavProperties.QuotaAvailable)!.Value);
        Assert.Equal("100", found.Element(DavProperties.QuotaUsed)!.Value);
        Assert.Equal("Sat, 07 Nov 2026 18:07:00 GMT", found.Element(D + "getlastmodified")!.Value);
        Assert.Equal("2026-11-07T18:07:00Z", found.Element(D + "creationdate")!.Value);
        Assert.Equal("0", found.Element(D + "isreadonly")!.Value);
        Assert.Equal("00000010", found.Element(Z + "Win32FileAttributes")!.Value);
        var missing = Props(f, 404);
        Assert.NotNull(missing.Element(D + "getcontentlength")); // a folder has no length and no entity tag
        Assert.NotNull(missing.Element(D + "getetag"));
        Assert.NotNull(missing.Element(X + "color"));

        var g = responses[1];
        Assert.Equal("/dav/My%20Drive/Reports/.%C3%9Cbersicht%20%F0%9F%98%80.txt", g.Element(D + "href")!.Value);
        found = Props(g, 200);
        Assert.Equal(".Übersicht 😀.txt", found.Element(D + "displayname")!.Value);
        Assert.Equal("1234", found.Element(D + "getcontentlength")!.Value);
        Assert.Equal("\"abc123\"", found.Element(D + "getetag")!.Value);
        Assert.Equal("text/plain", found.Element(D + "getcontenttype")!.Value);
        Assert.Empty(found.Element(D + "resourcetype")!.Elements());
        Assert.Equal("1", found.Element(D + "isreadonly")!.Value);
        Assert.Equal("00000023", found.Element(Z + "Win32FileAttributes")!.Value); // archive, read-only, hidden (a dot file)
        Assert.NotNull(Props(g, 404).Element(DavProperties.QuotaAvailable)); // no quota for this item
    }

    [Fact]
    public async Task Allprop_answers_the_properties_of_the_rfc_and_propname_only_names()
    {
        var file = new DavItem("a.txt", false, 3, "text/plain", "abc", Saturday, Saturday, NodeId: 6, NodePath: "/6/");
        var include = DavXml.PropFind(XDocument.Parse("<propfind xmlns=\"DAV:\" xmlns:z=\"urn:schemas-microsoft-com:\"><allprop/><include><z:Win32FileAttributes/><z:Nothing/></include></propfind>"))!;
        var all = Props((await RenderAsync(include, ("/dav/a.txt", new DavPropContext(file, [], null, DateTimeOffset.UtcNow)))).Root!.Element(D + "response")!, 200);
        foreach (var name in new[] { "creationdate", "displayname", "getcontentlength", "getcontenttype", "getetag", "getlastmodified", "resourcetype", "supportedlock", "lockdiscovery" })
        {
            Assert.NotNull(all.Element(D + name));
        }

        Assert.NotNull(all.Element(Z + "Win32FileAttributes")); // asked for with include
        Assert.Null(all.Element(D + "quota-used-bytes"));

        var names = (await RenderAsync(new DavPropFind(DavPropMode.PropName, []), ("/dav/a.txt", new DavPropContext(file, [], null, DateTimeOffset.UtcNow)))).Root!;
        var listed = Props(names.Element(D + "response")!, 200);
        Assert.NotNull(listed.Element(D + "getetag"));
        Assert.All(listed.Elements(), e => Assert.True(e.IsEmpty));
    }

    [Fact]
    public async Task A_large_multistatus_is_sent_in_pieces_and_stays_well_formed()
    {
        var body = new CountingStream();
        var now = DateTimeOffset.UtcNow;
        await using (var answer = new DavMultiStatus(body))
        {
            for (var i = 0; i < 5000; i++)
            {
                var item = new DavItem($"file {i}.txt", false, i, "text/plain", "sha" + i, Saturday, Saturday, i + 1, $"/{i + 1}/");
                DavProperties.WriteResponse(answer.Writer, "/dav/My%20Drive/" + DavPath.Escape(item.Name), new DavPropContext(item, [], null, now), DavPropFind.AllProp);
                await answer.FlushAsync(CancellationToken.None);
            }

            await answer.CompleteAsync(CancellationToken.None);
        }

        Assert.True(body.Writes > 10, "the answer goes out in pieces");
        var doc = XDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal(5000, doc.Root!.Elements(D + "response").Count());
        Assert.Equal("/dav/My%20Drive/file%204999.txt", doc.Root.Elements(D + "response").Last().Element(D + "href")!.Value);
    }

    [Fact]
    public void A_proppatch_answer_gives_every_property_its_status()
    {
        var xml = DavXml.PropPatchAnswer("/dav/My%20Drive/a.txt", [(Z + "Win32CreationTime", 200), (D + "getetag", 403), (Z + "Win32FileAttributes", 424)]);
        var response = XDocument.Parse(Encoding.UTF8.GetString(xml)).Root!.Element(D + "response")!;
        Assert.Equal("/dav/My%20Drive/a.txt", response.Element(D + "href")!.Value);
        Assert.NotNull(Props(response, 200).Element(Z + "Win32CreationTime"));
        Assert.NotNull(Props(response, 403).Element(D + "getetag"));
        Assert.NotNull(Props(response, 424).Element(Z + "Win32FileAttributes"));
    }

    [Fact]
    public void A_lock_answer_shows_the_lock()
    {
        var (locks, time) = NewLocks();
        var owner = new XElement(D + "owner", new XElement(D + "href", "Ada"));
        var made = locks.Lock(1, [10, 11], true, deep: false, "/dav/My%20Drive/a.txt", owner, TimeSpan.FromSeconds(600)).Lock!;

        var active = XDocument.Parse(Encoding.UTF8.GetString(DavXml.LockAnswer(made, time.Now + TimeSpan.FromSeconds(100)))).Root!
            .Element(D + "lockdiscovery")!.Element(D + "activelock")!;
        Assert.NotNull(active.Element(D + "lockscope")!.Element(D + "exclusive"));
        Assert.NotNull(active.Element(D + "locktype")!.Element(D + "write"));
        Assert.Equal("0", active.Element(D + "depth")!.Value);
        Assert.Equal("Second-500", active.Element(D + "timeout")!.Value);
        Assert.Equal(made.Token, active.Element(D + "locktoken")!.Element(D + "href")!.Value);
        Assert.Equal("/dav/My%20Drive/a.txt", active.Element(D + "lockroot")!.Element(D + "href")!.Value);
        Assert.Equal("Ada", active.Element(D + "owner")!.Value);
    }

    [Fact]
    public void An_error_names_the_precondition()
    {
        var root = XDocument.Parse(Encoding.UTF8.GetString(DavXml.Error("lock-token-submitted", "/dav/My%20Drive/a.txt"))).Root!;
        Assert.Equal(D + "error", root.Name);
        Assert.Equal("/dav/My%20Drive/a.txt", root.Element(D + "lock-token-submitted")!.Element(D + "href")!.Value);
    }

    // ---- sign-in and names ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Basic_credentials_are_read_as_utf8_or_latin1()
    {
        static string Basic(string text, Encoding encoding) => "Basic " + Convert.ToBase64String(encoding.GetBytes(text));

        Assert.True(DavAuthenticator.TryParseBasic(Basic("jürgen@example.com:pä:ss wörd", Encoding.UTF8), out var email, out var password));
        Assert.Equal("jürgen@example.com", email);
        Assert.Equal("pä:ss wörd", password);
        Assert.True(DavAuthenticator.TryParseBasic(Basic("ada@example.com:Grüße", Encoding.Latin1), out _, out password));
        Assert.Equal("Grüße", password);

        Assert.False(DavAuthenticator.TryParseBasic(null, out _, out _));
        Assert.False(DavAuthenticator.TryParseBasic("Bearer abc", out _, out _));
        Assert.False(DavAuthenticator.TryParseBasic("Basic not-base64!", out _, out _));
        Assert.False(DavAuthenticator.TryParseBasic(Basic("no-colon", Encoding.UTF8), out _, out _));
        Assert.False(DavAuthenticator.TryParseBasic(Basic(":password", Encoding.UTF8), out _, out _));
    }

    [Fact]
    public void The_folders_of_shared_drives_and_shared_items_get_unique_names()
    {
        var names = DavNames.Unique([(3, "Team", true), (1, "team", true), (7, "Report.docx", false), (9, "Report.docx", false), (12, ".profile", false), (13, ".profile", false)]);
        Assert.Equal("team", names[1]);
        Assert.Equal("Team (2)", names[3]);
        Assert.Equal("Report.docx", names[7]);
        Assert.Equal("Report (2).docx", names[9]);
        Assert.Equal(".profile", names[12]);
        Assert.Equal(".profile (2)", names[13]);

        Assert.Equal("a_b_c_", DavNames.Clean("a/b:c?", "x"));
        Assert.Equal("Drive 4", DavNames.Clean(" ... ", "Drive 4"));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    private static (DavLockManager Locks, ManualTime Time) NewLocks()
    {
        var time = new ManualTime();
        return (new DavLockManager(time), time);
    }

    private static async Task<XDocument> RenderAsync(DavPropFind find, params (string Href, DavPropContext Context)[] items)
    {
        using var body = new MemoryStream();
        await using (var answer = new DavMultiStatus(body))
        {
            foreach (var (href, context) in items)
            {
                DavProperties.WriteResponse(answer.Writer, href, context, find);
                await answer.FlushAsync(CancellationToken.None);
            }

            await answer.CompleteAsync(CancellationToken.None);
        }

        return XDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>The <c>prop</c> of the <c>propstat</c> with this status.</summary>
    private static XElement Props(XElement response, int status)
        => response.Elements(D + "propstat").Single(p => p.Element(D + "status")!.Value == DavXml.StatusLine(status)).Element(D + "prop")!;

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}

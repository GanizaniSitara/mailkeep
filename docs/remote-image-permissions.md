# Per-site picture permissions

Implemented 5 October 2026. Select **Image sites…** in the picture bar in the
reading pane or a separate message window. Each site has its own choice:

| Choice | Behaviour |
| --- | --- |
| Always allow | Load pictures automatically when viewing a message. |
| Ask | Wait for Load pictures; that click allows the site for that message only. |
| Never allow | Keep the site blocked, including when loading other pictures. |

Use **Save** to apply choices; **Cancel** leaves them unchanged. Each site has
three radio buttons. **Permit all** selects Always allow for every displayed site;
**Reject all** selects Never allow. Both allow individual adjustments before Save.
New sites use Ask until a choice is saved. Existing saved allowances are preserved and appear in the editor, where
they can be changed to Ask or Never allow. The editor remains available even when
all the message's sites are already allowed or blocked.

**Tools > Options > Image sites > Manage saved image sites…** reviews saved sites
without needing to find the original message. Save in the site dialog applies its
changes independently of the outer Options dialog. Sites are exact HTTP(S)
origins, including scheme, subdomain and non-default port; choices do not imply a
wildcard permission for related hosts.

Scope correction: **Image sites…** opened from a message shows only remote image
origins in that message's HTML. It no longer mixes in previously saved sites from
other mail. The dialog states that its saved permissions still apply across all
mail; Permit all / Reject all affect only displayed sites. The Options editor
continues to show all saved sites, with a distinct scope label. Neither list is a
list of confirmed trackers, and a tracker-list match is explicitly a suggestion.

Each row identifies a saved choice, a DNS-list recommendation (including its matched
domain and Block List Project source), or a possible unique identifier in that
message's image URL. The URL heuristic recognises recipient/user/token parameters,
UUIDs, long hex values and long mixed letter/digit opaque path/query values. It
shows the parameter name or path reason, never the identifier value. Legitimate
signature images and cache tokens can match too; this is a suggestion, not proof.
New matching sites start at Never allow; other new sites stay Ask. Saved choices
take priority, with **Use blocking suggestions** available to apply recommendations
explicitly. Suggestions alone do not change permissions or fetch images.
Recommendations and reasons appear beside the radio buttons: red for a suggested
block, amber for Ask when safety is unknown. Message rows also show their complete
image URLs in selectable, read-only, wrapping text boxes; long/multiple URLs can
be scrolled and copied. Viewing the text does not request the URLs.
The dialog opens at 70% of both dimensions of its owner's monitor working area,
centres within that area and refits when moved to another monitor. Columns and
recommendation wrapping follow its width; DPI changes refit within the new monitor.
Compact URL rows
and instructions leave more sources visible together; the site list has a wider
28px scrollbar with arrow/page controls and mouse-wheel scrolling.
DNS recommendations are enabled by default. Options → Image sites provides
**Use DNS block lists for image blocking recommendations** to turn them off;
Options OK saves the preference and Cancel leaves it alone. Disabling DNS
suggestions preserves saved permissions and URL-identifier suggestions. The site
dialog explicitly reports when DNS suggestions are disabled.

Visual validation: the native VM harness renders exact-size layouts for 1920×1160
landscape work areas, a 1200×1880 portrait area at (1920, -386), and a landscape
area with negative X coordinates. Screenshots were retrieved and visually inspected,
including a long identifier URL. These are simulated monitor work areas on the
single-display VM, not physical dual-monitor/DPI tests. The harness bypasses the
VM's native maximum window track size only for those screenshot renders.

The editor preselects Never allow for previously unsaved sites whose host or a
parent domain appears in the bundled **Block List Project tracking and analytics
list**, and labels the matching domain. Saved choices always take priority.
**Use tracker suggestions** selects Never allow on listed sites, including old
allowances, while leaving unlisted choices alone. Suggestions only become active
when saved; opening or cancelling the editor changes no permissions. An unlisted
site is not necessarily tracker-free, and a listed site may also serve wanted
content. This is a DNS-domain recommendation, not URL/content or CNAME detection.
No DNS resolver changes, runtime downloads or external classification requests.

Bundled snapshot retrieved 5 October 2026: 143,876 domains, from
https://github.com/blocklistproject/Lists/blob/ee3bdbaddf49c756cf3340bb2e6a9dee5282d1c6/tracking.txt
(upstream last modified 18 July 2026). Raw SHA-256:
`f1de1c81739ce2480b1dea890e9be6a1212008b19318b585102cc64e7013b0f1`.
Hosts-file entries are normalized to a sorted domain list in the embedded resource.
Upstream repository LICENSE is Unlicense and is bundled verbatim; upstream list
header labels its license MIT, retained in the resource attribution. Updating the
snapshot is an explicit development change, not an automatic updater.

The image fetcher checks every redirect target against the allowed origins before
requesting it. Cross-site cached images retain their origin dependencies and are
not reused if a dependency is no longer allowed. HTTPS-to-HTTP redirect refusal is
preserved. Policy changes refresh open mail views, and a stale in-flight fetch
cannot restore images after a site is revoked. Mail-page scripts remain disabled;
remote pictures are fetched in-process and inlined, with the page's image policy
blocking disallowed remote URLs.

Allowed pictures now appear individually as their downloads finish, in both the
reading pane and separate message windows. A slow or failing picture no longer
holds back successfully downloaded pictures. Updates retain the existing page
and scroll position; saved Ask and Never choices remain enforced.

The native `image-loading` regression saves mixed Allow/Ask/Never choices and
checks decoded images in WebView2, with one permitted response deliberately held.
The previous build fails while waiting for all responses; the fixed build shows
the completed permitted image before the held response is released. It then
checks one-time Ask loading and zero Never requests. The screenshot was retrieved
and visually inspected. Native `html-preview` and all 177 unit tests also pass.

Verification uses disposable stores and loopback HTTP sites: legacy permissions,
default Ask, independent per-site choices, Save/Cancel, reopening saved settings,
automatic/one-time loading in the main and reader windows, pruning an old allow,
zero Never requests, redirects, cache revocation and an in-flight policy change.
The existing image-fetch smoke covers src/srcset/background/CSS/encoded-query
images and cache reuse; the existing remote-image UI smoke covers the real bar,
editor selection, persistence and standalone reader. No live mail is changed and
no external picture or mail service is contacted by these tests.

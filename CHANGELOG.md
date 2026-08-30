# Changelog

Release notes for Console.Lib, one entry per `Major.Minor`, newest first.

The version NUMBER is not here: it lives in `src/Directory.Build.props` (`VersionMajorMinor`), and the
build job reads that property back rather than restating it, so a package can never declare a version
this file disagrees with. Bump it there and add the entry here, in the same commit.

Breaking changes carry their migration steps in [MIGRATION.md](MIGRATION.md); this file says what
changed and why.

## 4.30

Local Markdown links didn't open. `[docs](docs/foo.md)` rendered as an OSC 8 hyperlink the same as any
`https://` link, but the target was the raw href verbatim -- a bare relative path, not a URI -- and
Windows Terminal (rightly) refuses to Ctrl+Click one, reporting "invalid link". Images already had a
host-supplied resolver for exactly this (`MarkdownImageOptions.Resolver`); links had no equivalent.

`MarkdownRenderer.Render` / `RenderLines` (and `MarkdownWidget.LinkResolver`) now take an optional
`Func<string, string>? linkResolver`, threaded through every place a link can appear -- paragraphs,
headings, lists, table cells. It rewrites only the OSC 8 target; the visible `(url)` text after the
label still shows the original href, so plain-text dumps and copy-paste are unaffected.

mdcat wires one up: an href that already parses as an absolute URI (`http(s)://`, `mailto:`, an
already-`file://` link, a rooted Windows path) passes through unchanged; a `#anchor`-only href is left
alone too, since there's nothing on disk to point at; everything else resolves against the document's
directory -- the same base dir local images already resolve against -- into a `file://` URI.

## 4.29

The debug inspector can drive a DRAG. It had exactly one pointer verb, `click`, which injects a press
and a release at the same cell with no motion between them, so no terminal app could have a drag
synthesized against it at all -- which meant anything that follows the pointer (a drag ghost, a
rubber band, a pan) was unverifiable end-to-end no matter how well it worked.

Four verbs now: `press`, `move`, `release`, and an atomic `drag` built from them.

**Prefer the stepped verbs, and this is the whole reason they exist.** An atomic `drag` lands in the
input queue all at once, so a consumer that coalesces motion -- drop the render when another event is
already queued, carry its damage forward -- correctly renders NONE of the intermediate positions. The
gesture completes, every report arrives, and nothing mid-drag is ever painted. So `drag` can prove a
gesture but can never prove the thing that follows the pointer; only one event in flight at a time
reproduces a human drag closely enough to observe it. Coalescing is what a well-behaved consumer does,
so this is not a niche case.

Two details keep the synthesized stream faithful to what a terminal can actually emit:

- **`move` is refused when no button is held**, rather than injected anyway. Mode 1002 is BUTTON-motion
  tracking, so a terminal never sends a hover report. Synthesizing one would let hover-driven behaviour
  pass a test through a door that is nailed shut in production -- the failure this prevents is a GREEN
  test, not a red one. This is a deliberate divergence from SdlVulkan.Renderer's inspector, which does
  have a bare `move`, because on a GPU host hover is real.
- **Motion is reported once per cell CROSSED**, not once per interpolation step: a terminal reports a
  position when it changes, and its resolution is a cell. Asking for more `steps` than the path has
  cells yields the cells, not repeats.

The MCP server exposes all four as tools.

## 4.28

Honours `TextTrim.Middle`, added in DIR.Lib 8.9 for a run whose two ENDS both carry meaning -- a file
path, or a filter-curve provenance line naming a product and its measured peak. Unlike `Shrink` and
`None`, this policy needs no degradation on a character grid: it is a character-count cut like `Start`
and `End`, so `CellLayout` implements it exactly rather than falling back to end-trimming.

The tie-break matches the one the single-cell case already made: the ellipsis takes the odd cell and
the head keeps the remainder, so at width 2 the tail is what goes.

## 4.27

Rebuilt against DIR.Lib 8.8, whose layout capture is now unconditional -- the arranged tree is what
damage-based repaint diffs against, so it is no longer gated on the inspector being attached. Nothing
in Console.Lib changes behaviour: a cell surface already paints by diffing (`CellBuffer`), which is the
model the pixel side has now adopted, and `LayoutInspection` survives as an obsolete no-op so no call
site here needed touching.

## 4.26

CellLayout maps DIR.Lib 8.3's IconKind.Plus and Minus: ASCII "+", and U+2212 MINUS SIGN rather than
the ASCII hyphen. The pair exists so the two marks line up in a stepper, and hyphen-minus is the one
character that reliably breaks that -- most faces draw it shorter and higher than the plus's crossbar,
being a hyphen first and an operator second. U+2212 sits on the same axis by definition, and comes
from the Mathematical Operators block the list icon already draws from.

It also fills a hole that had been open for four minor versions. CaretUp and CaretDown arrived in
DIR.Lib 7.23 and were never mapped here, so both rendered as the "?" placeholder on every terminal.
Nothing failed, which is exactly the problem: the fallback exists so a forgotten kind degrades rather
than throws, and that also makes it silent. They are U+25B2 and U+25BC now, filled to match the pixel
drawings, whose reason for being filled rather than chevrons applies here too -- one cell holds a
two-stroke chevron as a hairline with a hole in it, and hinting closes the hole first.

So the change that matters is the test. EveryIconKindHasAGlyph enumerates IconKind and fails on any
kind that falls back, which turns "the next person remembers" into something the build says out loud.
The vocabulary is documented as costing a drawing upstream and a glyph here, and nothing was
collecting the second half.

Requires DIR.Lib 8.3, a FLOOR rather than a follow, since the two enum members do not exist before it.
The pin therefore cannot move ahead of a published DIR.Lib: 8.3 goes to nuget.org first, this second.

## 4.25

No-code lockstep rebuild against DIR.Lib 7.29. Required rather than bookkeeping this time:
7.29 is binary-breaking (TextInputRenderer.Render returns the caret rect and takes a fallback
resolver, DropdownMenuState is generic, IPixelWidget gains Ui), and CellLayout links against
that assembly. Nothing in the TUI changes -- the cell painter reads TextInputRenderer.Colors,
which is untouched -- and the point of moving is that the graph keeps resolving ONE DIR.Lib
rather than two.

## 4.24

CellLayout paints DIR.Lib 7.28's Layout.Content.TextInput, so an editable field is ONE
declaration that works on a terminal and a GPU surface alike. Three things a cell surface
has to answer differently, each a decision rather than a limitation: the FILL is the field
(a one-row box cannot also carry a border, so focus is the background alone), the caret is
the terminal's REAL one via SetCaret (a painted block can be neither thin nor blinking), and
an over-long value SCROLLS rather than ellipsizing -- an ellipsis in an editable field sits
exactly where the text being edited belongs, and the caret would have no real cell to land
on. CellLayout.HitTest derives a field's hit from the CONTENT, matching the pixel painter, so
a field is clickable because it is a field. CellLayout.TextInputs(arranged) is the cell
answer to IPixelWidget.GetRegisteredTextInputs: it feeds TextInputInteraction's TabFields, so
a terminal host shares Tab cycling instead of hand-rolling it, in the order the fields were
painted. Describe names a focused field, because "which box has the keyboard" is the
question a text-input bug starts from and is otherwise invisible in a text dump.

## 4.23

Follows DIR.Lib to 7.21, whose Layout.CrossAlign this library gets for free: the cell
painter arranges through the same engine, so a TUI row can now centre its controls across
its axis with .CrossCenter() instead of padding the container. DIR.Lib 7.20 is in this range
too (an icon draws at its declared size, not stretched to its cell), which affects only the
pixel painter. No code change here.

## 4.22

The cell half of DIR.Lib 7.19's three theme marks. CellLayout maps ThemeLight /
ThemeSystem / ThemeDark to the geometric-shapes circles U+25CB / U+25D0 / U+25CF, so a theme
control authored once paints a sun, a divided disc and a crescent on a GPU surface and an
empty, half and full circle here. Deliberately NOT the sun and moon of U+2600..263F, which is
where the pixel drawings point: that block is pictographic and a monospace face is under no
obligation to carry it, and not gambling on coverage is the whole reason an icon names its
meaning instead of spelling a codepoint. The circles keep the light-to-dark ordering, which
is what survives the crossing to a single cell.

## 4.21

CellLayout paints DIR.Lib 7.18's Content.Icon: the same node the pixel painter constructs
from rectangles becomes a centred glyph here, because a character grid cannot draw those
rectangles. Grid is U+259E (quadrant blocks), List is U+2261, Auto is plain ASCII A -- one
cell has no room for its viewfinder brackets, and the meaning is what has to survive the
crossing. Every glyph is drawn from the ranges a terminal font is relied on to carry, which
is the same well every border, scrollbar and tree marker in this library already draws from.
Re-pins DIR.Lib 7.14 -> 7.18. Also adds a shared CellBufferViewport to the test project:
four test files each carried a private copy, and a fifth was the wrong answer.

## 4.20

Lockstep rebuild against DIR.Lib 7.14 (7.11 -> 7.14). No Console.Lib code change. Two of the
three minors in that range change measured text WIDTH, which reaches this package because it ships
pixel painters (RgbaImageRenderer, SixelRgbaImageRenderer) as well as the cell grid. The CELL surface
is untouched by both: there one character is one cell, whatever the font's metrics say.
DIR.Lib 7.12 is additive -- TextInputRenderer takes a palette, the shape TabBar got in 7.10 -- and is
the upstream half of this package's own TextInputBar theming.
DIR.Lib 7.13 bounds the SDF atlas's rasterize retry (a glyph that can never rasterize is given up on
instead of pinning IsDirty true forever) and ALSO, unannounced at the time and only written up in
7.14's notes, takes a whitespace advance from the font instead of borrowing the 'n' glyph's. In DejaVu
every measured space had been 1.99x too wide, so on a pixel surface a space-padded run now measures
nearly half a space narrower per space. Any column lined up with space padding therefore moves; pad
with U+2007 FIGURE SPACE instead, which a font defines to advance like a digit.
DIR.Lib 7.14 makes TextFit.ShrinkToWidth return a size it actually measured, so a run fitted with
TextTrim.Shrink no longer draws a fraction of a pixel past the rect it was just fitted to. Shrink is
opt-in and the default is End, so a caller that never asks for it renders byte-identically.
Released alongside SdlVulkan.Renderer 7.11 and WebGl.Renderer 1.18 rather than alone: a consumer
holding two backends built against different DIR.Lib minors unifies on the higher one by luck, not
by intent, which is how WebGl.Renderer twice ended up two minors behind.

## 4.19

TextBar.Caret(column, style): the caret for a bar whose text the CALLER composes. TextInputBar
derives the column from its own TextInputState, but a bar packing several fields into one line (tianwen's
site row: Lat / Lon / Elev) composes the string itself, so it passes the column in. The bar owns the
CLIPPING decision, because it owns the truncation -- a column the ellipsis ate, or one past the room the
right text left, withdraws the caret instead of parking it on a cell that shows something else. Caret is
opt-in per bar and, once called, that bar reasserts or withdraws every Render: a bar that never calls it
never touches the caret, so a status bar painted after the focused editor cannot erase the editor's
caret. A row squeezed to zero width withdraws too. ADDITIVE. 7 test cases.

## 4.18

The caret is the terminal's REAL cursor. TextInputBar and TextArea gain an opt-in
Caret(CaretStyle) that parks the actual cursor at the insertion point (DECSCUSR shapes 1-6;
BlinkingBar is the thin editor bar) instead of painting a reverse-video cell -- the terminal draws
and blinks the caret itself, which no cell paint can imitate: a painted caret can never be thinner
than a cell, nor blink without repaint traffic. Plumbing: ITerminalViewport.SetCaret/HideCaret
(default no-ops, so fakes and capture surfaces ignore them), TerminalViewport translates like any
cell write, and VirtualTerminal applies the request at the END of Flush -- after the cell diff,
retracted during the paint so a visible cursor never rides the runs (or a raw Sixel blit). Shape and
visibility emit only on a CHANGE (the pen's emit-less rule); position emits every time, because the
paint just moved the real cursor as a side effect of writing. Sticky until HideCaret: the focus
owner decides when the caret goes away, since an on-demand painter may not re-render every frame.
ColorMode.None suppresses it like every other escape. Dispose restores the user's configured shape
(DECSCUSR 0) iff one was ever set -- the shape is NOT alternate-screen state and would otherwise
survive into the shell prompt -- and hands a normal-buffer session its cursor back. Widgets land the
caret on the exact cell the painted block occupied: label + separator for the bar (surrogate pair =
one cell), gutter + the click mapping's cell accounting for the area (tab = TabWidth, pair = 1), so
a click and the caret it places round-trip to the same cell; a cursor clipped off the content area
withdraws the caret rather than leaving it standing on the wrong cell. Terminals without DECSCUSR
show their default cursor at the parked cell -- degraded to a block, never wrong. ADDITIVE: new
CaretStyle enum + default interface members. 24 test cases.

## 4.17

ConsoleSize reads the terminal's real size when stdout is REDIRECTED, which is the case
System.Console cannot answer: WindowWidth/Height size via GetStdHandle(STD_OUTPUT_HANDLE), and that
handle IS the pipe once output is piped or captured, so the properties throw "handle is invalid"
exactly when something is consuming our output. mdcat caught that and rendered at a hard-coded 80
columns; the console was still attached and still 120 wide, only the handle asked was the wrong one,
and because the number is a fallback rather than a failure nothing ever looked wrong -- output was
merely narrower than the window forever. CONOUT$ opened with CreateFileW names the ATTACHED CONSOLE's
active screen buffer independent of stdout, so GetConsoleScreenBufferInfo still answers; size comes
from srWindow (Right-Left+1, the edges being INCLUSIVE) and NOT from dwSize, whose height is the
scrollback -- commonly 9001 rows against a 25-row window, a wrong answer that looks like a right one.
Read at call time, so a live RESIZE lands on the next read instead of being baked in at startup; a
width passed at invocation could not do that. The handle is opened once per process and cached,
failure included (no console at all: a detached service, CI), because VirtualTerminal.Size is read on
every idle poll and nothing here attaches/detaches a console mid-run. VirtualTerminal keeps setting
_noConsole under a redirected stdout even though CONOUT$ can now measure the window: the DA1 and
cell-size probes WRITE to stdout and read the reply back, so clearing it would inject escape
sequences into the consumer's pipe and then stall ~200ms awaiting an answer that cannot arrive.
Measuring is safe; asking is not. mdcat's GetConsoleWidth and its byte-identical twin in
examples/MarkdownConsole both delegate to ConsoleSize.GetWidth() and are deleted. Non-Windows
behaviour is unchanged (System.Console throughout; no /dev/tty work). ADDITIVE: new public type, no
existing signature moves. 6 tests over the srWindow arithmetic -- the inclusive +1 and the dwSize
confusion are the two ways to be plausibly wrong here; the CONOUT$ handle path needs a real console
and stays untested by design, as the rest of WindowsConsoleInput is.

## 4.16

CellLayout states what the two NEW TextTrim members mean on a character grid (DIR.Lib 7.8, which
teaches the pixel painter to fit a run to its arranged rect). Neither is expressible here, so each gets
the nearest cell behaviour instead of falling through Ellipsize's not-Start branch by accident: Shrink
asks for a smaller face and a grid has exactly one size, so it end-trims (a shorter WHOLE run being
unavailable, the head is the next best thing); None asks to overflow, which here would overwrite the
neighbouring cells, so it hard-clips -- the same cut with NO ellipsis, because nothing should claim a
removal the author asked not to make. That degradation is what keeps one authored tree meaning the same
thing on a pixel surface and a cell one, which is the whole premise of sharing the tree. Repins DIR.Lib
7.7.* -> 7.8.*. 3 tests, one of which pins Shrink against End so the two cannot silently diverge.
Later in 4.16, no X.Y bump and NO published value changes: VersionPrefix and AssemblyVersion moved out
of the csprojs and into src/Directory.Build.props, next to the number they derive from. Both were
already derived here, so Console.Lib still builds 4.16.0.0 -- this is about there being one place
instead of four to forget. DIR.Lib and SdlVulkan.Renderer had the same restatement NOT derived and
shipped 6.4.0.0 and 6.11.0.0 for two majors; the family now states the rule once per repo, in the props.
One behaviour change, local only: Console.Lib.Inspector no longer carries VersionPrefix 1.0.0. It was
there to mark the inspector as separately versioned, but CI's solution-wide -p:Version overrode it on
every run, so nuget.org has only ever had it in lockstep (4.3.1341 first, 4.16.1511 latest, never a
1.0.0). The literal delivered no separate versioning and only made local packs disagree with published
ones; a local pack now reads 4.16.0, matching CI. Genuine divergence would need its own
VersionMajorMinor and pack step.

## 4.14

CellLayout honours Layout.Content.Text.Trim (DIR.Lib 7.7), so a run says which end it loses
when it does not fit. The painter always had to cut somewhere -- a cell surface measures in whole
characters -- and it always cut the tail, which is right for a label and useless for a path: every
path on a machine shares its head, so "C:\Users\seb\repos\so…" identifies nothing where
"…\ftw\Program.cs" is the part being read. Callers used to pre-truncate against the column width to
get the other behaviour, and a row's width is exactly what the engine took over in 4.10, so the
workaround stopped existing and path columns silently lost their filenames. At a width of one there
is no room for a glyph AND an ellipsis, so the cell goes to the surviving end's character rather than
to a lone "…" that says less. ADDITIVE: TextTrim.End is the default and the previous behaviour, so
every existing run paints byte-identically. Repins DIR.Lib 7.5.* -> 7.7.*. 10 tests.
4.15! - FIX: TreeView drew its scroll bar wherever the row left the cursor. The bar is written with a
bare Write, which was correct until 4.10 because a row was then one string of exactly contentWidth
cells emitted in sequence, so the cursor arrived at the bar's column by itself. Rows are layout trees
now: CellLayout positions per text run and leaves the cursor after the LAST GLYPH it drew, so the bar
landed immediately right of each row's rightmost text -- a different column on every row, reading as a
stray block beside every label rather than as a scroll bar. ScrollableList already positioned its own
bar; TreeView was missed in the 4.10 port. The widget's own class comment has documented
"col width-1: scrollbar" throughout, so the contract never changed -- only the code drifted from it.
It appears ONLY once the tree overflows, and looks exactly like a font that cannot draw a glyph, which
is how it survived being stared at directly. First TreeView tests in the repo: 4, two of which fail
without the fix, over a fixture with deliberately RAGGED label widths -- uniform labels put the
misplaced bar in one column and look identical to a correct one.

## 4.12

Lockstep rebuild against DIR.Lib 7.5, no code change here. 7.5 resolves a font by its DECLARED
family rather than by file identity, so every face in a family is reachable and a run that a face
cannot cover falls back per run instead of per request. Rebuilt to keep the whole sibling family on
one DIR.Lib.
4.13! - OSC 8 HYPERLINKS on the cell path. A node states a link by carrying a HitResult.LinkHit -- the
hit it already needed for the click -- and CellLayout paints that leaf's text inside an OSC 8 pair,
resolved through the same nearest-enclosing walk as the background, so a link stated on a wrapper
reaches the text under it. Reusing the hit rather than adding a Layout.Node property is what makes the
drawn region and the clickable region the same arranged rect by construction. Cell gains Link, and
CellBuffer MODELS OSC 8 instead of giving up on it: a link used to make the pen unmodellable, so every
linked cell was Opaque and re-emitted every frame -- invisible on one link, and on a file list where
every row carries one it bypasses the diff for the whole column while the emitted-cell count still
looks small. A linked row now diffs like any other. BREAKING: ICellSink gains SetLink(string?) with no
default implementation -- a sink that dropped it would emit frames that look complete and have lost
every link in them. The console sink emits an id= so a link the diff splits across runs stays one link
to the terminal. mdcat's inline links ride the same sequences (Osc8) and become diffable with them; the
inspector's `cell` verb reports "link" when there is one.

## 4.11

Lockstep rebuild against DIR.Lib 7.4, no code change here. 7.4 closes the asymmetry around
CellMeasureContext: since 4.10 a PIXEL-authored tree could be carried onto cells via
CellMeasureContext.PixelAuthored, but nothing carried a CELL-authored tree the other way, so
PixelMeasureContext gains per-axis scales and a matching CellAuthored factory (same nominal 8x16
cell). It also adds PixelWidgetBase Arrange/Paint/RenderLayout overloads that take the measure
CONTEXT rather than a bare dpiScale, which is what makes per-axis safe: dpiScale used to be threaded
separately into the measure context and the paint loop, two copies kept in step by hand, and a
per-axis context would have turned that into text painted at a size it was never measured at. All
additive; the scalar overloads delegate to the context ones with an isotropic context, so existing
callers paint byte-identically. Rebuilt so the whole sibling family sits on one DIR.Lib.

## 4.10

List and tree rows are LAYOUT TREES. IRowFormatter is deleted; ITreeNode.FormatNodeContent is
deleted. Both are replaced by a method returning a DIR.Lib.Layout.Node and taking the shared new
RowContext(Selected, SelectedColumn, ColumnCount). No compatibility shim, on purpose: a
default-implementation bridge is what lets a codebase sit half-ported, and a row that quietly kept the
old shape would keep all three defects below while appearing to work. The old contract was
`string FormatRow(int width, ColorMode, ...)` documented as "must include VT escape codes and pad to
the full width", so every row hand-rolled its own layout, padding, truncation and escapes. That cost:
(1) an inline button on a row had NO hit region -- no arranged rect to bind to, so a caller re-derived
its columns beside the drawing code, and since the row's usable width is not the viewport width (the
scrollbar takes a column once the list overflows) a right-aligned button drifted by one column exactly
when the list scrolled; (2) a row could not state a colour it did not own, because foreground-only
writes leaned on leftover SGR state -- which a real terminal forgives and 4.8's diffing cell buffer
cannot; (3) the same row is often also a GPU row, so authoring it twice let the two drift, which is how
a terminal row ended up missing a button its GPU twin had. `width` and `ColorMode` are gone as
parameters (the widget owns the rect it already computed; CellLayout owns the pen), and the
three-overload cascade collapses to fields on RowContext so the next capability adds a field rather
than a fourth rung an implementation can silently ignore. RegisterRowHits / RegisterRowSpanHits /
RowSpan are DELETED -- hits ride on .Clickable and resolve through the new DispatchRowHit, which tests
against the trees as last ARRANGED. That is what makes it correct: the four ways the old helpers could
silently disagree with the paint (pixel origin from the viewport offset, the header row, the SCROLLED
item index, the scrollbar column) are no longer expressible. HitTestRow is unchanged. ITreeNode keeps
Children / HasChildren / EnsureChildrenLoaded defaults -- those are optional BEHAVIOUR, not a second
way to satisfy one obligation. Port recipe in MIGRATION.md.

## 4.9

EnableCellBuffer seeds the size that Flush's resize-detection compares against. It did not, so the
field stayed (0,0) and the FIRST Flush of a buffered app always read as a RESIZE: it called Resize again,
which reallocates the back buffer and fills it with blanks, discarding everything the frame had already
painted. What reached the screen was only whatever happened to be written AFTER that flush. The reason
this hid behind 4.8's five fixes is that the two consumers that found those repaint unconditionally every
frame, so frame two restored the loss and the bug was a one-frame flicker at startup. An app that paints
ON DEMAND and leaves cells standing has no frame two: periodic-table-viewer renders on selection change,
so its table, header, detail panel and status bar were destroyed permanently and the terminal showed only
its orbital panel -- which survived precisely because it blits Sixel BEFORE writing its text, putting that
text after the wiping flush. Diagnosed with 4.8's own accounting: ~12.7k flushed cells for a 202x63 grid
is exactly one full-screen repaint, and CollectFlushDiagnostics named the surviving runs as the orbital
column alone. NOT unit-tested: the seed is only observable through Flush, which needs Size, which needs a
real console -- the same wall 4.8 hit for its cursor-move fix, and there is no pure function to extract
here. Verified against the running TUI through the debug inspector instead.

## 4.8

The diffing cell buffer survives contact with a real app. TianWen's TUI turned it on and exposed
five latent bugs, four of them the same shape: RELYING ON LEFTOVER TERMINAL STATE, which a live terminal
forgives and a buffer that must name a colour per cell cannot. (1) VtStyle treated alpha zero as a colour
and emitted it as opaque BLACK; a terminal cell does not composite, so alpha zero means UNSTATED and now
emits SGR 39/49 (the terminal's default), which the buffer's parser round-trips as a modelled pen rather
than degrading the cell to Opaque. (2) CellLayout painted text foreground-only, inheriting the background
from whatever SGR the previous write left behind -- so a row's colour depended on which SIBLING painted
first, and rows following a Reset went black-on-black (TianWen's Guider/Camera rows rendered as gaps).
Paint now resolves a text cell's background from the TREE via a depth-keyed stack of enclosing
backgrounds; the rounded-corner clip states its quadrant's backdrop the same way. (3) The cell sink
emitted ReverseOn and never ReverseOff, so one reversed cell (a text cursor) inverted everything painted
after it -- headers became solid bars. Reverse is now stated in both directions on change. (4) THE BIG
ONE: TerminalViewport.SetCursorPosition called parent.Flush() per cursor move, which on a buffered
terminal ships the HALF-PAINTED diff -- background blanks over the old text, then each label flushed back
one by one. On screen that is erase-then-redraw at exactly the repaint cadence: TianWen's once-per-second
top-bar flicker, which survived four fixes aimed at the emitted bytes because the bytes were right and
their TIMING was not. A cursor move now does nothing but translate and forward; only the frame's owner
flushes, and the sink's own moves ride the SAME byte stream as the glyphs (CUP escape, not Win32
SetCursorPosition -- one ordered sequence, one delivery mechanism). (5) Unrelated but found by the same
user: the byte table special-cased 0x08 as Backspace, SHADOWING the general 0x01..0x1A -> letter+Ctrl
rule three lines below it -- Ctrl+H was the one letter no app could bind, while the Backspace KEY sends
DEL 0x7F, which the table already handled. The special case is deleted, nothing else changed.
Diagnostics shipped with the fixes, because the flicker was only found by measuring: VirtualTerminal
exposes FlushedCellsTotal / FlushedOpaqueCellsTotal (TOTALS, deliberately -- a per-last-flush read is how
the mid-paint flush hid from the first version of the accounting while being the whole problem), and
CellBuffer.CollectFlushDiagnostics records each emitted run's position and text, answering WHICH cells
went out -- the question the front buffer cannot answer after the fact, because its final state always
looks right. Re-pins DIR.Lib 7.2.* -> 7.3.*.

## 4.7

The inspector's `key` verb carries MODIFIERS, so a chorded binding is drivable at last. It hardcoded
ConsoleModifiers zero, which made every chord unreachable -- Chess.Console flips its board on Ctrl+F and
no driver could ask for it. The real byte parser already handled this (a terminal sends Ctrl+letter as one
control byte, decoded to (ConsoleKey.A + n, Control) at VirtualTerminal.cs:732), so what is injected now is
exactly what a keypress produces and the app's genuine binding is exercised, not an inspector-only path.
Spelling follows the SDL inspector's `mods` string -- substring-matched, case-insensitive, so "Ctrl",
"ctrl+shift" and "CtrlShift" all work -- so one convention covers both inspectors. DIVERGES from SDL in
one way, on purpose: unrecognised modifier text is REFUSED rather than resolved to None. Dropping it would
deliver a bare key, and a bare key is often a different valid binding rather than a no-op (bare `f` is
chess's file-f selector), so the failure would read as the app ignoring a correct chord. The reply echoes
the resolved modifiers for the same reason. The sidecar's `key` tool gains an optional `mods` parameter and
a Json.Obj to build it, mirroring SdlVulkan.Renderer.Inspector's.

## 4.6

The MCP sidecar stops using reflective JSON. Library UNCHANGED; the sidecar rides this version
prefix, so shipping it needs a bump. It escapes its own strings now instead of calling
JsonSerializer.Serialize, which throws at runtime under trimming or AOT (both set
JsonSerializerIsReflectionEnabledByDefault=false) even for a plain string. That is not hypothetical: the
same call in the APP half failed exactly that way against Chess.Console, surfacing as a socket that closed
the instant it was written to. A `dnx` tool is a plausible future AOT candidate, so leaving it armed on the
driver side made no sense. It cannot reuse DebugInspectorCore.Quote -- that is behind #if DEBUG and so is
absent from a published DIR.Lib -- hence a local copy, which also keeps this project referencing nothing
from the framework. (SdlVulkan.Renderer's in-process inspector already avoids the serializer for the same
reason, with a comment about IL2026/IL3050; its sidecar still has the call.)

## 4.5

The discovery `kind` for a terminal is "tui", not "console". DIR.Lib 7.2 introduced the field so a
sidecar can filter replies to surfaces it knows how to drive, and "tui"/"sdl"/"webgl" is the vocabulary
worth keeping -- "console" named the library rather than the surface, and pairs badly with "sdl". Renamed
while nothing outside these repos depends on it. The sidecar accepts BOTH, because 4.4 shipped the older
word for one afternoon and dropping it would make that build invisible rather than merely mislabelled.

## 4.4

ConsoleDebugInspector.Detached: the same verbs with NO transport, no TCP listener and no multicast
bind. Added because the 4.3 tests opened a real socket for EVERY assertion, and almost none of them were
about the wire: what a row of cells reads back as, how a key name maps, where a click's pixel centre
lands. Driving that through a socket makes each one depend on port availability and on joining a
multicast group, for nothing. Invoke() was already the seam; only construction had to stop starting the
server. The unit tests now run socket-free (11 in ~50ms, down from ~300ms) and ONE test carries
Trait Category=Functional for the wire contract -- id echoed back, unknown verb answered as an error.
`dotnet test --filter Category!=Functional` is now a meaningfully hermetic run.

## 4.3

A front/back CELL BUFFER with a diffing flush, plus the terminal backend for DIR.Lib 7.1's
DebugInspectorCore. Console.Lib was immediate-mode: a widget wrote its whole region as one string of SGR
plus padded text, which is invisible for a redraw the user asked for and very visible once a second on a
clock -- every cell in the row repainted, padding included, which reads as a FLASH. TianWen hit exactly
that. CellBuffer emits only what changed: a 40-cell clock row rewritten in full per tick emits ONE cell.
Write() parses the SGR Console.Lib itself generates back into a pen; anything outside that vocabulary
(an OSC hyperlink from mdcat, a cursor move, \e[1m bold) makes the pen unmodellable and its cells Opaque
-- always re-emitted, never diffed, degrading to the old behaviour instead of being modelled wrongly. An
unrecognised SGR PARAMETER goes opaque too, because a pen that is only mostly right shows up as a MISSING
repaint, the one failure a diff must not have. A Sixel blit writes pixels through OutputStream, which the
buffer never sees, so MarkImage declares that region and the diff never writes a glyph into it.
OPT-IN via VirtualTerminal.EnableCellBuffer(): every consumer currently relies on immediate writes, so
flipping the default would change mdcat and every hosted app at once. Unbuffered cost is one null test.
ConsoleDebugInspector is the inspector's first backend, and its CELL PLANE is what a GPU surface cannot
offer: the screen readable as TEXT ("the status bar reads `White to move.`") straight off the FRONT
buffer -- what was actually emitted, not a parallel model that can drift. Verbs: screen, row, cell,
appState, inputLog, key, click, size. Injected input goes through a DEBUG-only queue on VirtualTerminal,
drained ahead of the real stream so a synthetic event cannot land mid escape sequence. Inspector and
queue are both #if DEBUG (401 tests in Release, 412 in Debug); the cell buffer ships in both.
Console.Lib.Inspector 1.0 ships alongside: a published MCP-server package (PackageType=McpServer,
consumed via `dnx Console.Lib.Inspector`) that DISCOVERS running Debug-build TUI apps and drives them.
It references nothing from Console.Lib -- it speaks the JSON protocol -- because the in-process half is
#if DEBUG and a sidecar linking the library would be coupled to a configuration it cannot see. Tools:
list_instances, screen/row/cell, app_state, input_log, key/keys/click, size, ping. Filters discovery to
kind=="console" (DIR.Lib 7.2), so a GPU app answering the same multicast group is not offered `screen`.
Re-pins DIR.Lib 6.23.* -> 7.2.* for DebugInspectorCore + discovery.

## 4.2

SixelEncoder.Encode takes a `reserved` colour list: entries claim palette indices 0..n-1 before
any frequency ranking, and keep them whether or not the frame contains a pixel of them. The palette is
otherwise chosen purely by pixel FREQUENCY, which is right for flat regions and wrong for anything that
carries meaning in a few pixels. Measured on a real chess board render: 1966 distinct colours for 255
slots, THREE of them (background + the two square fills) covering ~95% of the surface, and the
255th-ranked colour occupying twelve pixels -- the rest of the budget consumed by glyph antialiasing. A
selection tint or a 3px last-move border is ranked against that tail on area alone, and when it loses,
FindNearest snaps it to the closest survivor; since the survivors are overwhelmingly near-duplicates of
the background and the board, losing does not shift the hue slightly, it makes the accent INVISIBLE.
Reserving also makes the index stable, which two things need: a partial strip re-derives its palette
from that strip's histogram alone and could otherwise represent the same accent differently from the
full frame before it, and an animated stream would reshuffle indices every frame as the histogram
shifts (shimmer, plus no possibility of inter-frame delta encoding). A list of 255 entries leaves the
frequency pass nothing to allocate and so IS a fixed palette -- the limit case is deliberate, so
animation needs no separate mode. SixelRgbaImageRenderer.ReservedColors feeds BOTH encode paths.
Default empty = historical behaviour byte for byte, which the pinned output hashes assert.

## 4.1

ALSO: CellLayout softens a filled rect's corners with three-quadrant blocks (U+2599, U+259B,
U+259C, U+259F) instead of the arc glyphs 3.14 introduced. A filled rect and a bordered one want
different glyphs and this is the filled one: an arc is a thin STROKE, so a corner cell drawn that way is
~90% parent colour and reads as a bite punched out of the card rather than a softened corner (on
TianWen's home board -- a blue card on a near-black page -- it read as damage). A three-quadrant block
covers three quarters of the cell, so the corner loses a QUARTER cell rather than a whole one, which is
the smallest bite a character grid can express. No arc branch remains: both FillCells call sites are
gated on a fill and the layout DSL has no border chrome, so an unfilled rounded box is currently
unexpressible -- if Layout.Node gains a border, the arcs are what should render ITS corners, at that
call site. RoundCorners -> ClipFilledCorners (private).

ConsoleInputMapping stops classifying mouse MOTION as a press. xterm mode 1002 reports pointer
movement with the HELD BUTTON still in the button field and IsRelease false, and ToInputEvent asked only
"is this a release?", so every motion report became an InputEvent.MouseDown at the pointer. MouseEvent has
documented IsMotion as a drag report since it was added; the mapping simply never consulted it, and
InputEvent.MouseMove existed in DIR.Lib and was never produced. For a consumer that acts on MouseDown this
is not cosmetic: a click whose pointer drifts one pixel is delivered TWICE, and a drag is delivered as a
click on every cell crossed. Found in Chess.Console, where the second delivery landed after the engine had
already replied and so re-selected the piece the player had just moved (legal-move hints and all), and
where dragging with a piece selected would play a move at whatever square the pointer passed over.
Thumb-drag consumers improve too: ListScrollController wants MouseMove and was never getting it in a
terminal. Motion now maps to MouseMove; the wheel check still comes first, so a wheel report carrying the
motion bit stays a Scroll.
4.0! - a layout tree can finally cross surface kinds. CellMeasureContext hardcoded one design unit =
one cell, which is right for every hand-written TUI tree (RowH(1) is one row) and wrong for a tree
authored on a pixel surface (RowH(16) is one line of text). The convention belongs to the TREE, so it
is now a constructor parameter with two presets: CellAuthored (1:1, the default) and PixelAuthored (a
nominal 8x16 cell). MAJOR because this is the capability the layout stack had been claiming without
delivering -- Layout.Node was always shared and Engine.Arrange always generic, but no tree had ever
crossed between a pixel surface and a terminal, since a 250-unit card became 250 COLUMNS: type-correct
and geometrically meaningless. Needs DIR.Lib 6.23 because the conventions differ by a DIFFERENT factor
PER AXIS (250 units is 31 columns across but 8 rows down) and one scalar could not say that, hence
ToSurfaceX / ToSurfaceY; the axis-free ToSurface (corner radius) resolves against the COLUMN size, the
finer of a terminal's two resolutions. Proven end to end by TianWen's home board: one HomeBoardLayout
tree, one palette, one card projection, rendered by both the GPU tab and the TUI tab. Existing callers
are unaffected -- the default is still 1:1 -- so the break is in what the type MEANS, not in what
compiles.

## 3.16

ScrollableList.HitTestRow: the same geometry read in the opposite direction, for a host that
resolves a click when one arrives rather than registering regions up front and so has nowhere to put
a ClickableRegionTracker. Pixel point in; item index, item, and the column WITHIN the content area
plus the content width out. Null for the header, the scrollbar column, outside the viewport, or a row
past the last item. The scrollbar column is why this belongs in the widget: Widget.HitTest reports it
like any other column, so a host dividing a row into fields by Viewport.Size.Width treats clicks on
the track as content -- and whether a scrollbar shows depends on the item count, so it changes under
the caller. Returning the content width alongside the column is what keeps them out of it.

## 3.15

ScrollableList.RegisterRowSpanHits: clickable spans WITHIN a row (inline buttons), the
column-range counterpart to 3.14's whole-row RegisterRowHits. Both now share one ForEachVisibleRow
walk, so the four things that must be right (origin from the viewport OFFSET, the header row, the
SCROLLED item index, the scrollbar column) are computed once instead of per call site. Spans clamp
to the content width, so int.MaxValue means "to the end of the row" and cannot overlap the
scrollbar. New public RowSpan record struct.

## 3.14

CellLayout honours DIR.Lib 6.21's Layout.Node.CornerRadius: a non-zero radius knocks one
cell off each corner of a background fill and draws an arc glyph there (U+256D..U+2570), so one
tree renders rounded on both the pixel and cell surfaces. The radius MAGNITUDE is ignored -- a grid
cannot round by fractions of a cell, and Unicode has arc forms for corners only (no rounded tee or
cross), so one cell is the honest approximation; skipped below 3x3 where the corners are the shape.
ScrollableList.RegisterRowHits binds a clickable region per visible row, so a host stops
reconstructing the widget's geometry: the pixel origin is viewport OFFSET x cell size, row 0 is
ScrollOffset and not item 0, the header steals a row, and the scrollbar column must be left alone
or the thumb can never be grabbed. Every host had a slightly different version of that arithmetic.
Lockstep rebuild against DIR.Lib 6.21.

## 3.13

Shared table rendering. BorderStyle / BorderChars (Light, Heavy, Double, Rounded,
Ascii) and TextTable.Render are the one place box-drawing junctions and column-width
arithmetic live; the markdown renderer's four private table helpers were deleted in favour
of it, so mdcat picks it up transitively with no change of its own. Column widths measure
via VisibleLength, so SGR escapes in a cell do not inflate the column. TerminalViewport.UpdateGeometry
widens internal -> public so a layout tree can host a widget at a Fill leaf and re-point it
per frame. Lockstep rebuild against DIR.Lib 6.20.

## 3.3

Lockstep rebuild against DIR.Lib 6.0 (layout namespace + Layout.Builder DSL).

## 3.1

Markdown images. MarkdownRenderer gains a MarkdownImageOptions
parameter: an image alone on its own line (![alt](src)) rasterises via the
same Sixel / sextant / half-block path as display math (BoxRenderer.EncodeImage),
decoding through StbImageSharp; inline / unresolvable images fall back to alt
text. mdcat wires a local-file resolver (no network). Repins DIR.Lib -> 5.1.*
for the new MdImage AST node.

## 2.17

ScrollableList and TreeView gain opt-in AutoHandleWheel / WheelStep
properties. When set, HandleMouse auto-routes wheel events (button 64/65)
into HandleWheel at the configured step. Default off keeps existing wheel
consumers — which do their own button-64/65 dispatch — unaffected.

## 2.16

MarkdownWidget gains MathMode + MathFontPath properties, so widget
consumers can opt display-math blocks ($$ … $$) into the pixel-render path
(Sixel / sextant / half-block) without bypassing the widget and reaching
for MarkdownRenderer.RenderLines directly. Defaults stay at single-row
Unicode, so existing callers are unaffected.

## 2.15

Mhchem Phase-2 picks up box layout in display math. TryRenderMathBox
now expands \ce{...} via DIR.Lib.Markdown.Mhchem.ToLatex (paired with the
DIR.Lib 4.2 chem-to-LaTeX rewrite). Display chem inside $$ … $$ rasterises
via the same Sixel / sextant / half-block dispatcher as math.

# Hunt

Hunt is a local VS Code extension for accessible, voice-first workspace
search. It presents results in a native tree view and places a unique letter
hint at the left of every matching line.

The native tree supports VS Code keyboard navigation, high-contrast themes,
and screen readers. Result accessibility labels include the hint, file, line,
column, and matching text rather than relying on color or position alone.

## Voice commands

- `bar hunt` — focus whichever Hunt screen is visible
- `hunt <letters>` — open the matching line
- `hunt side <letters>` — open it in a side editor
- `hunt next` / `hunt last` — move through results
- `hunt case`, `hunt word`, `hunt expression` — toggle search options
- `hunt include` / `hunt exclude` — set file glob filters
- `hunt refresh` / `hunt clear`

Type or paste into the search box and press Enter. The whole sidebar switches
to the results tree; it does not show search and results as separate sections.
In results, press Escape to switch the whole sidebar back to the search box.
Pressing Escape in the search box cancels and returns focus to the editor.

The search screen shows every search mode as ON or OFF, its current include or
exclude value, and the corresponding voice command. Results are ordered by
textual match quality; when quality is tied, any non-data file appears above a
configured data-file extension. Use `F4` for the next result and `Shift+F4` for
the previous result.

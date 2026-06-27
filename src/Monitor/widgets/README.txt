michka dashboard widgets
========================

Drop one folder per widget in this directory. The folder name is the widget type id.
Widgets appear in the swipe-down Widgets page chooser; selections are per host.

Each widget folder may contain:
  widget.json - metadata: { "name", "description", "author", "version", "tag" }
  widget.js   - REQUIRED. Registers the widget renderer/lifecycle (see the contract below)
  widget.css  - optional styles, injected once when the widget type is first used
  icon.svg    - optional icon shown on the chooser entry
  preview.png - optional thumbnail

Files are served at /widgets/<folder>/<file>.

widget.js contract
------------------
The script is injected once per page; it must register itself by id:

  Michka.widget("my-widget", {
    // Build the card body. Return an HTML string or a DOM node. (required)
    render(ctx) { return `<div>Hello ${ctx.esc(ctx.host)}</div>`; },
    // Start timers / fetch data after the body is in the DOM. (optional)
    mount(ctx) { /* ctx.el is the .widget-body element */ },
    // Tear down timers when the card is removed or the page closes. (optional)
    unmount(ctx) { },
  });

ctx gives you: host (selected host name), widget (the instance { id, type }),
el (the body element, mount/unmount only), meta (the widget.json fields), and helpers
t (i18n), esc (HTML-escape), fmtBytes, and api(path) (fetch JSON from the hub, same origin).

michka UI templates
===================

Drop one folder per template in this directory. The folder name is the template id.
Select a template from the Settings page (Templates group). Empty = the built-in default.

Each template folder may contain:
  template.json  - metadata: { "name", "description", "author", "version" }
  style.css      - layered over the default stylesheet when this template is active
  script.js      - injected after the default app.js when this template is active
  preview.png    - thumbnail shown on the template card in Settings

Files are served at /templates/<folder>/<file>.

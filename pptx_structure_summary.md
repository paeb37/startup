# PPTX Package Overview

- `.pptx` files are OPC zip packages with `[Content_Types].xml` listing MIME mappings and explicit overrides for parts.
- Root `/_rels/.rels` points to the main presentation document and document properties in `docProps/` (core, extended, custom metadata, thumbnail).
- `customXml/` stores auxiliary data; each item has its own `.rels` describing links that the deck references for tags or structured content.
- `ppt/presentation.xml` defines slide order, masters, defaults; `ppt/_rels/presentation.xml.rels` resolves `rId` entries to slides, masters, themes, notes, tables, tags.
- Each slide in `ppt/slides/` has DrawingML markup plus a companion `.rels` tying in its layout, images from `ppt/media/`, and other resources.
- `ppt/slideMasters/` and `ppt/slideLayouts/` supply reusable templates; notes and handout parts live in `ppt/notes*` and `ppt/handoutMasters/`.
- Supporting assets sit under `ppt/media/`, `ppt/embeddings/`, `ppt/theme/`, `ppt/tags/`, `ppt/tableStyles.xml`, `ppt/presProps.xml`, and `ppt/viewProps.xml`.
- Relationship files mesh the package together: Office resolves content by following `rId` entries rather than direct paths, allowing parts to be renamed or moved within the zip.

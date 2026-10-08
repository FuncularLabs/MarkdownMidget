# Printing code

For TEST-PLAN-1.0.md, area PRN. Work on a copy.

Between them these blocks hold every kind of highlighted token that used to print in
the theme's screen colours: selectors, `!important`, URLs, variables, a regular
expression, a symbol, diff lines, an XML declaration, a doctype, an entity and a CDATA
section.

```css
a.link > p:hover { color: var(--accent) !important; background: url(paper.png); }
```

```scss
$accent: #0550ae;
a { color: $accent; }
```

```javascript
const words = /gpqy/gi;
```

```ruby
status = :ready
```

```diff
- a removed line
+ an added line
```

```xml
<?xml version="1.0"?>
<!DOCTYPE note>
<note>&amp; <![CDATA[raw text]]></note>
```

# Space marks

With formatting marks on, in the Markdown source view, each line says which of its spaces show a dot.

This line ends in two spaces, both dotted:  
This line ends in one space, dotted: 
Two spaces sit here:  between these words, both dotted.
The next line holds only three spaces, all dotted.
   
Single spaces between words are never dotted.

- A list item, nothing dotted
  - nested two spaces in, nothing dotted
    - nested four spaces in, nothing dotted

1. An ordered item, nothing dotted
  - two spaces in, short of the text of the item above: both leading spaces dotted

A paragraph whose next line starts with a space and a tab:
 	the leading space is dotted and the tab shows an arrow

| Table | Padding    |
| ----- | ---------- |
| cells | not dotted |

```text
aligned  = "in code only the trailing spaces on the next line are dotted"
padding  = 2  
```

The last line, nothing dotted.

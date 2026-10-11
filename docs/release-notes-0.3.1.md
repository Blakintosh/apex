# Apex 0.3.1

A fixes release.

- **Decimal symbol warning.** If Windows uses a comma as the decimal symbol, Apex says so when it starts: many
  Black Ops III mod tools misread numbers that way and can write wrong values into assets and builds. The banner
  opens the Region settings; set the decimal symbol to a full stop. Apex itself is not affected.
- **Fix:** picking a material category could raise an "Index was outside the bounds of the array" error from the
  dropdown. The pick is now applied once the dropdown has finished committing it.

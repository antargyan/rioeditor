# Showcase documents for App Store screenshots. Written to look like real work, not lorem ipsum:
# a reviewer sees the app doing something a person would actually do with it.
HERO = """# Redesigning the onboarding flow

We cut the sign-up funnel from **five steps to two**. This note captures what changed,
what it cost, and what we watch next.

## What changed

- Removed the workspace-naming step entirely
- Merged email and password onto one screen
- Deferred the team invite until *after* first value

> The best step in a funnel is the one you delete.

The full spec lives in the [design doc](https://example.com/spec).
"""

DATA = """## Measuring the change

| Metric | Before | After |
| --- | ---: | ---: |
| Completion | 41% | 68% |
| Median time | 3m 12s | 51s |
| Drop-off | 59% | 32% |

The funnel is instrumented at each step, so the lift is attributable rather than inferred.

```csharp
public static double CompletionRate(int started, int finished) =>
    started == 0 ? 0 : (double)finished / started;
```
"""

TASKS = """## What happens next

- [x] Ship the two-step flow
- [x] Instrument every funnel step
- [ ] A/B the deferred invite prompt
- [ ] Localise the validation copy

Conversion lift is $\\Delta = 0.68 - 0.41 = 0.27$, sustained over three weeks.

```mermaid
graph LR
  A[Sign up] --> B[First value]
  B --> C[Invite team]
```
"""

# The iPad canvas is roughly twice the height of the phone's, so the phone documents leave a
# lot of dead white space. These combine sections to fill the page properly.
IPAD_A = HERO + "\n" + DATA
IPAD_B = DATA + "\n" + TASKS
IPAD_C = TASKS + """
## Open questions

1. Does the deferred invite depress *team* creation over a longer window?
2. Is the 51s median hiding a bimodal distribution?
3. What breaks if a user arrives from a shared link rather than the marketing site?

Owner: **Priya** · Review: first Tuesday of the month
"""

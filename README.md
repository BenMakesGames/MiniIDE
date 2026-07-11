**tl;dr:** this is an experimental C# IDE, intended to be used alongside an external agentic AI that runs in its own thang (such as Claude Code's TUI (flawed though it may be)).

> 🧚 **hey, listen!** [you can support my development of open-source software on Patreon](https://www.patreon.com/BenMakesGames), and/or [check out my game Astromino, on Steam!](https://store.steampowered.com/app/4644350/Astromino/)

### why?

I love Rider - it's been my favorite C# IDE for years - but like all IDEs, it's a resource hog that eats laptop batteries for breakfast, and I get why: Rider & Visual Studio are built to support every use case anyone ever wanted and asked for.

but of all the features Rider offers, I've only used very few; with agentic AIs, I use fewer still.

(I've tried vscode various times over the years: its UI/UX is simply not to my liking, and their C# support has always lagged, _and_ it's an electron app?! that's extra weight++!)

so, like any ridiculous dev, I've decided to "just" make my own...

![](docs/screenshot.png)

### my must-haves

ultimately: to reach a point where I'm comfortable uninstalling Rider.

| feature                                | status                  |
| -------------------------------------- | ----------------------- |
| syntax highlighting (C#, JSON, XML)    | ✅                       |
| highlighting warnings & errors         | ❌                       |
| jump to declaration                    | ✔ - could use a UI pass |
| find usages                            | ✔ - could use a UI pass |
| global search, w/ regex if you want it | ✔ - could use a UI pass |
| solution-wide warnings & errors view   | ✅                       |
| NuGet package management               | ✔ - could use a UI pass |
| a decent-ish & modern-ish look & feel  | ✅                       |

### my nice-to-haves

| feature                                                | status |
| ------------------------------------------------------ | ------ |
| _any_ git integration?                                 | ❌      |
| navigate interface implementations & inheritance trees | ❌      |
| nice test result UI (not just console output)          | ❌      |

### my GTFOs

1. custom window chrome
2. scanning the entire solution when the IDE starts up
3. settings that require a built-in search tool


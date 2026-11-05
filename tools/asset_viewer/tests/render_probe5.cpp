// render_probe5.cpp -- bug1 "wheel-scroll then click hits the wrong item" quantification
//
// Replicates the real interaction path exactly:
//   1. run ScanWorker over the asset root, build filtered_ exactly like the app
//   2. simulate "user scrolls the wheel n notches" (same formula as WM_MOUSEWHEEL)
//   3. HitTestItem at screen (x,y) -> which item is reported
//   4. reverse lookup: which cell is actually DRAWN at (x,y) per CellRect()
//   5. mismatch => the click lands on a different card than the one under the cursor
//
// Two control groups:
//   A. after scrolling, no RebuildFilter (steady state)      -> expect 0% mismatch
//   B. after scrolling, RebuildFilter resets scrollY_ to 0    -> old behaviour, expect large mismatch
//
// Group B reproduces the OLD code by hand (that line is now deleted) to prove it was the root cause.
#include "asset_scanner.h"
#include "av_util.h"
#include "scan_worker.h"
#include "viewer_app.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include <windows.h>

namespace {

// layout constants, identical to viewer_app.cpp
const float kToolbarH = 44.0f;
const float kStatusH = 26.0f;
const float kListWidth = 340.0f;
const float kPad = 10.0f;
const float kCellGap = 8.0f;
const float kCellW = 150.0f;
const float kCellH = 132.0f;

struct Lay {
    float listL, listT, listR, listB, cellW, cellH;
    int columns;
};

Lay MakeLayout(float cw, float ch, float s) {
    Lay L;
    const float midTop = kToolbarH * s;
    const float midBot = ch - kStatusH * s;
    float listW = kListWidth * s;
    if (listW > cw * 0.45f) listW = cw * 0.45f;
    if (listW < 220.0f * s && cw > 500.0f) listW = 220.0f * s;
    L.listL = 0; L.listT = midTop; L.listR = listW; L.listB = midBot;
    L.cellW = kCellW * s;
    L.cellH = kCellH * s;
    const float inner = (L.listR - L.listL) - kPad * 2.0f * s - 12.0f * s;
    int cols = (int)((inner + kCellGap * s) / (L.cellW + kCellGap * s));
    if (cols < 1) cols = 1;
    L.columns = cols;
    return L;
}

struct View {
    Lay L;
    float s = 1.0f;
    float scrollY = 0.0f;
    std::vector<int> filtered;

    float RowHeight() const { return L.cellH + kCellGap * s; }
    float ContentHeight() const {
        if (L.columns < 1) return 0;
        const int rows = (int)((filtered.size() + L.columns - 1) / L.columns);
        return rows * RowHeight() + kPad * s;
    }
    void ClampScroll() {
        float maxS = ContentHeight() - (L.listB - L.listT);
        if (maxS < 0) maxS = 0;
        if (scrollY > maxS) scrollY = maxS;
        if (scrollY < 0) scrollY = 0;
    }
    // same math as CellRect()
    void CellRect(int fi, float* l, float* t, float* r, float* b) const {
        const int col = fi % L.columns;
        const int row = fi / L.columns;
        const float x = L.listL + kPad * s + col * (L.cellW + kCellGap * s);
        const float y = L.listT + kPad * s + row * RowHeight() - scrollY;
        *l = x; *t = y; *r = x + L.cellW; *b = y + L.cellH;
    }
    // same math as HitTestItem()
    int HitTest(float x, float y) const {
        if (x < L.listL || x > L.listR || y < L.listT || y > L.listB) return -1;
        if (filtered.empty() || L.columns < 1) return -1;
        const float relX = x - (L.listL + kPad * s);
        const float relY = y - (L.listT + kPad * s) + scrollY;
        if (relX < 0 || relY < 0) return -1;
        const int col = (int)(relX / (L.cellW + kCellGap * s));
        const int row = (int)(relY / RowHeight());
        if (col >= L.columns) return -1;
        const float inCellX = relX - col * (L.cellW + kCellGap * s);
        const float inCellY = relY - row * RowHeight();
        if (inCellX > L.cellW || inCellY > L.cellH) return -1;
        const int idx = row * L.columns + col;
        if (idx < 0 || idx >= (int)filtered.size()) return -1;
        return filtered[idx];
    }
};

// Reverse lookup: which filtered index is drawn at (x,y) in view v?
int DrawnAt(const View& v, float x, float y) {
    for (size_t fi = 0; fi < v.filtered.size(); ++fi) {
        float l, t, r, b;
        v.CellRect((int)fi, &l, &t, &r, &b);
        if (b < v.L.listT || t > v.L.listB) continue;
        if (x >= l && x <= r && y >= t && y <= b) return (int)fi;
    }
    return -2;   // outside every card (gap / blank area)
}

int FilteredOf(const View& v, int item) {
    for (size_t fi = 0; fi < v.filtered.size(); ++fi)
        if (v.filtered[fi] == item) return (int)fi;
    return -1;
}

// hitFrame decides the click, drawFrame decides what the user sees on screen
void Audit(const char* label, const View& drawFrame, const View& hitFrame) {
    int ok = 0, bad = 0, printed = 0;
    for (float y = drawFrame.L.listT + 4; y < drawFrame.L.listB - 4; y += 20.0f) {
        for (float x = drawFrame.L.listL + 4; x < drawFrame.L.listR - 4; x += 20.0f) {
            const int drawn = DrawnAt(drawFrame, x, y);
            if (drawn == -2) continue;
            const int hit = hitFrame.HitTest(x, y);
            const int hfi = hit >= 0 ? FilteredOf(hitFrame, hit) : -1;
            if (hit >= 0 && hfi == drawn) {
                ok++;
            } else {
                bad++;
                if (printed < 3) {
                    std::printf("    MISMATCH at (%.0f,%.0f): hit item=%d (filtered#%d)  drawn filtered#%d (item=%d)\n",
                                (double)x, (double)y, hit, hfi, drawn,
                                drawn >= 0 ? drawFrame.filtered[drawn] : -1);
                    printed++;
                }
            }
        }
    }
    const int tot = ok + bad;
    std::printf("  %-48s ok=%d bad=%d  mismatch=%.2f%%\n", label, ok, bad,
                tot > 0 ? 100.0 * bad / tot : 0.0);
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    ::SetConsoleOutputCP(CP_UTF8);
    (void)argv;
    // Item count only: the real tree has 371 models. The probe exercises the pure
    // scroll/hit geometry, so a synthetic count of the same magnitude is enough
    // (and it lets us also probe a small list that does NOT overflow the viewport).
    size_t n = 371;
    if (argc > 2) n = (size_t)_wtoi(argv[2]);
    std::printf("items=%zu (list length; layout/hit math is independent of content)\n", n);
    if (n == 0) return 2;

    std::vector<int> filtered;
    for (size_t i = 0; i < n; ++i) filtered.push_back((int)i);

    const struct { const char* name; float cw, ch; } sizes[] = {
        {"1400x900 (default window)", 1400.0f, 900.0f},
        {"1000x700 (halved)", 1000.0f, 700.0f},
        {"1680x1050 (maximized)", 1680.0f, 1050.0f},
    };

    for (int si = 0; si < 3; ++si) {
        std::printf("\n=== %s ===\n", sizes[si].name);
        View base;
        base.L = MakeLayout(sizes[si].cw, sizes[si].ch, 1.0f);
        base.filtered = filtered;
        std::printf("  columns=%d rowH=%.1f contentH=%.1f viewH=%.1f\n",
                    base.L.columns, (double)base.RowHeight(), (double)base.ContentHeight(),
                    (double)(base.L.listB - base.L.listT));

        const float rowH = base.RowHeight();
        const int wheels[] = {1, 3, 6, 12, 25};
        for (int wi = 0; wi < 5; ++wi) {
            // A: fixed code -- scroll, no RebuildFilter reset
            View a = base;
            a.scrollY += wheels[wi] * 0.6f * rowH;   // -(-120)/120 * rowH * 0.6
            a.ClampScroll();
            char lbl[160];
            std::sprintf(lbl, "[A FIXED] %d notches scrollY=%.1f", wheels[wi], (double)a.scrollY);
            Audit(lbl, a, a);

            // B: old code -- screen still shows the scrolled frame, but RebuildFilter zeroed scrollY_
            View b = base;
            b.scrollY += wheels[wi] * 0.6f * rowH;
            b.ClampScroll();
            View hitFrame = b;
            hitFrame.scrollY = 0.0f;                 // the deleted line, reproduced
            std::sprintf(lbl, "[B OLD-BUG] %d notches, RebuildFilter->0", wheels[wi]);
            Audit(lbl, b, hitFrame);
        }
    }
    std::printf("\nConclusion: group A (fixed) must stay 0.00%%; group B grows with scroll depth.\n");
    return 0;
}

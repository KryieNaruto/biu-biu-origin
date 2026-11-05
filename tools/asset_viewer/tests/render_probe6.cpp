// render_probe6.cpp -- bug4: does the animation block in the info panel fit?
//
// DrawInfoPanel() lays the right column out from ry = p.t + 10 + 26 (title),
// then adds: header(19) + up to 6 names(19 each) + overflow(19) + buttons(26+8) + progress(~24).
// The whole panel is clipped with PushAxisAlignedClip(layout_.info), so anything past
// p.b is silently cut. This probe computes required vs available height for every
// window size and reports the shortfall, plus which rows of the animation list survive.
#include <cstdio>
#include <cmath>
#include <algorithm>

namespace {

const float kToolbarH = 44.0f;
const float kStatusH = 26.0f;
const float kListWidth = 340.0f;
const float kPad = 10.0f;

struct R { float l, t, r, b; float W() const { return r - l; } float H() const { return b - t; } };

struct L {
    R list, preview, info;
    int columns;
};

L ComputeLayout(float cw, float ch, float s) {
    L o;
    R toolbar{0, 0, cw, kToolbarH * s};
    R status{0, ch - kStatusH * s, cw, ch};
    const float midTop = toolbar.b;
    const float midBot = status.t;
    float listW = kListWidth * s;
    if (listW > cw * 0.45f) listW = cw * 0.45f;
    if (listW < 220.0f * s && cw > 500.0f) listW = 220.0f * s;
    o.list = R{0, midTop, listW, midBot};
    o.preview.l = listW; o.preview.t = midTop; o.preview.r = cw;
    float infoH = (midBot - midTop) * 0.42f;
    if (infoH < 150.0f * s) infoH = 150.0f * s;
    if (infoH > (midBot - midTop) * 0.7f) infoH = (midBot - midTop) * 0.7f;
    o.preview.b = midBot - infoH;
    o.info = R{listW, o.preview.b, cw, midBot};
    const float inner = o.list.W() - kPad * 2.0f * s - 12.0f * s;
    int cols = (int)((inner + 8 * s) / (150 * s + 8 * s));
    if (cols < 1) cols = 1;
    o.columns = cols;
    return o;
}

// Vertical space the right column needs, mirroring DrawInfoPanel exactly
float RightColumnNeeded(const L& lay, float s, int clipCount) {
    const float lineH = 19 * s;
    float y = lay.info.t + 10 * s + 26 * s;   // pad + title line
    float ry = y;
    ry += lineH;                              // "动画（N）" header
    const int maxShow = 6;
    ry += std::min((int)clipCount, maxShow) * lineH;
    if ((int)clipCount > maxShow) ry += lineH;  // "… 还有 N 个"
    ry += 26 * s + 8 * s;                     // buttons + gap
    ry += 24 * s;                             // progress bar row (track + label)
    return ry - lay.info.t;
}

// How many animation rows are actually inside the clip rect?
int RowsVisible(const L& lay, float s, int clipCount) {
    const float lineH = 19 * s;
    float ry = lay.info.t + 10 * s + 26 * s + lineH;   // after header
    int vis = 0;
    for (int i = 0; i < clipCount && i < 6; ++i) {
        if (ry + lineH <= lay.info.b + 0.5f) vis++;
        else break;
        ry += lineH;
    }
    return vis;
}

} // namespace

int wmain() {
    const struct { const char* n; float cw, ch; } sizes[] = {
        {"1400x900 (CreateMainWindow)", 1400, 900},
        {"1200x800", 1200, 800},
        {"1000x700", 1000, 700},
        {"900x600 (user shrinks it)", 900, 600},
        {"800x600", 800, 600},
        {"1680x1050 (maximized)", 1680, 1050},
    };
    const int clipCounts[] = {14, 15, 18, 36};

    std::printf("%-30s %8s %8s  %s\n", "window", "infoH", "needed", "result");
    for (int si = 0; si < 6; ++si) {
        const L lay = ComputeLayout(sizes[si].cw, sizes[si].ch, 1.0f);
        for (int ci = 0; ci < 4; ++ci) {
            const int cc = clipCounts[ci];
            const float need = RightColumnNeeded(lay, 1.0f, cc);
            const int vis = RowsVisible(lay, 1.0f, cc);
            std::printf("%-30s %8.1f %8.1f  clips=%d -> rowsShown=%d/%d %s\n",
                        sizes[si].n, (double)lay.info.H(), (double)need, cc, vis,
                        std::min(cc, 6),
                        need > lay.info.H() + 0.5f ? "  *** CLIPPED ***" : "");
        }
        std::printf("    info rect = (%.0f,%.0f)-(%.0f,%.0f)  preview=%.0fx%.0f  list cols=%d\n",
                    (double)lay.info.l, (double)lay.info.t, (double)lay.info.r, (double)lay.info.b,
                    (double)lay.preview.W(), (double)lay.preview.H(), lay.columns);
    }

    // Also: does the preview pane keep a sane aspect for the 3D view?
    std::printf("\npreview pane aspect ratios (model is fit via min(halfV, halfH)):\n");
    for (int si = 0; si < 6; ++si) {
        const L lay = ComputeLayout(sizes[si].cw, sizes[si].ch, 1.0f);
        std::printf("  %-30s %.0fx%.0f  aspect=%.3f\n", sizes[si].n,
                    (double)lay.preview.W(), (double)lay.preview.H(),
                    (double)(lay.preview.W() / lay.preview.H()));
    }
    return 0;
}

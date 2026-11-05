// probe_hit.cpp —— 验证「滚轮滚动后点击命中」是否与视觉位置一致（bug 1 回归）。
//
// 做法：复刻 ViewerApp::CellRect 的几何计算，对多个滚动位置，
// 取每个单元格中心点，反查「这个点落在哪个 index」，
// 断言反查结果 == 原 index。任何不一致都会打印出来。
//
// 用法：probe_hit.exe
#include <cmath>
#include <cstdio>
#include <vector>

// 与 viewer_app.cpp 保持一致的布局参数（若源码改动需同步）
namespace {
constexpr float kPad = 8.0f;
constexpr float kCellGap = 8.0f;
constexpr float kCellW = 104.0f;
constexpr float kCellH = 132.0f;

struct Rect { float l = 0, t = 0, r = 0, b = 0; };

struct ListGeom {
    float listL = 240.0f, listT = 100.0f, listR = 1400.0f, listB = 900.0f;
    int columns = 5;
    float scrollY = 0.0f;
    float scale = 1.0f;

    float RowHeight() const { return kCellH + (kCellGap + kPad) * scale; }
    float CellW() const { return kCellW * scale; }
    float CellH() const { return kCellH * scale; }
    float ViewH() const { return listB - listT; }

    float ContentHeight(int itemCount) const {
        if (columns < 1) return 0.0f;
        const int rows = (itemCount + columns - 1) / columns;
        return rows * RowHeight() + kPad * scale;
    }

    // 与 ViewerApp::CellRect 完全一致
    Rect CellRect(int index) const {
        Rect out;
        if (columns < 1) return out;
        const int col = index % columns;
        const int row = index / columns;
        const float x = listL + kPad * scale + col * (CellW() + kCellGap * scale);
        const float y = listT + kPad * scale + row * RowHeight() - scrollY;
        out.l = x; out.t = y;
        out.r = x + CellW(); out.b = y + CellH();
        return out;
    }

    // 与命中测试一致的「反查」：给定屏幕点，找出落在哪个 index
    int HitTest(float px, float py, int itemCount) const {
        for (int i = 0; i < itemCount; ++i) {
            const Rect c = CellRect(i);
            if (px >= c.l && px < c.r && py >= c.t && py < c.b) return i;
        }
        return -1;
    }
};

void ClampScroll(ListGeom* g, int itemCount) {
    const float contentH = g->ContentHeight(itemCount);
    float maxScroll = contentH - g->ViewH();
    if (maxScroll < 0) maxScroll = 0;
    if (g->scrollY > maxScroll) g->scrollY = maxScroll;
    if (g->scrollY < 0) g->scrollY = 0;
}
} // namespace

int main() {
    const int kItems = 371;
    ListGeom g;
    int checked = 0, bad = 0;

    // 多个滚动位置：顶部、滚动若干格、滚动到底
    std::vector<float> scrolls;
    scrolls.push_back(0.0f);
    for (int k = 1; k <= 12; ++k) scrolls.push_back(k * g.RowHeight() * 1.5f);
    scrolls.push_back(1e9f);   // 触发 clamp 到底

    for (float s : scrolls) {
        g.scrollY = s;
        ClampScroll(&g, kItems);
        int local = 0, localBad = 0;
        for (int i = 0; i < kItems; ++i) {
            const Rect c = g.CellRect(i);
            // 只看完全落在列表视口内的单元格
            if (c.t < g.listT || c.b > g.listB) continue;
            if (c.l < g.listL || c.r > g.listR) continue;
            const float cx = (c.l + c.r) * 0.5f;
            const float cy = (c.t + c.b) * 0.5f;
            const int got = g.HitTest(cx, cy, kItems);
            ++checked;
            ++local;
            if (got != i) {
                ++bad;
                ++localBad;
                if (localBad <= 3) {
                    std::printf("  MISMATCH scrollY=%.2f expect=%d got=%d at (%.1f,%.1f)\n",
                                g.scrollY, i, got, cx, cy);
                }
            }
        }
        std::printf("scrollY=%8.2f  visible=%3d  mismatch=%d\n", g.scrollY, local, localBad);
    }

    std::printf("\nchecked=%d mismatch=%d  hit-rate=%.2f%%\n", checked, bad,
                checked ? 100.0 * (double)(checked - bad) / (double)checked : 0.0);
    return bad == 0 ? 0 : 1;
}

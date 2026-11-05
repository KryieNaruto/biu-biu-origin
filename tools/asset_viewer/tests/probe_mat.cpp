// probe_mat.cpp —— 打印模型材质与顶点色的实际数值，定位「颜色丢失」在哪一层。
#include "model_data.h"

#include <windows.h>
#include <cstdio>

int main() {
    int argc = 0;
    LPWSTR* argv = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    if (!argv || argc < 2) { std::printf("usage: probe_mat.exe <model>\n"); return 2; }
    int n = ::WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, nullptr, 0, nullptr, nullptr);
    if (n <= 0) { std::printf("path convert failed\n"); return 2; }
    std::string path((size_t)n, '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, &path[0], n, nullptr, nullptr);
    while (!path.empty() && path.back() == '\0') path.pop_back();
    ::LocalFree(argv);
    std::printf("argc=%d path=%s\n", argc, path.c_str());

    av::ModelData md;
    if (!av::LoadModel(path, &md) || !md.ok) {
        std::printf("load failed: %s\n", md.error.c_str());
        return 1;
    }
    std::printf("model=%s tris=%u verts=%zu mats=%u textures=%u\n", path.c_str(), md.triangleCount,
                md.mesh.vertices.size(), md.materialCount, (unsigned)md.textures.size());
    for (size_t i = 0; i < md.mesh.materials.size(); ++i) {
        const av::Material& m = md.mesh.materials[i];
        std::printf("  mat[%zu] name='%s' base=(%.4f, %.4f, %.4f, %.4f) tex=%d rough=%.3f metal=%.3f\n",
                    i, m.name.c_str(), m.baseColor[0], m.baseColor[1], m.baseColor[2], m.baseColor[3],
                    m.diffuseTex, m.roughness, m.metallic);
    }
    for (size_t i = 0; i < md.mesh.subMeshes.size() && i < 8; ++i) {
        std::printf("  sub[%zu] name='%s' mat=%d idxOff=%u idxCount=%u\n", i,
                    md.mesh.subMeshes[i].name.c_str(), md.mesh.subMeshes[i].materialIndex,
                    md.mesh.subMeshes[i].indexOffset, md.mesh.subMeshes[i].indexCount);
    }
    for (size_t i = 0; i < md.textures.size() && i < 4; ++i) {
        const av::TextureInfo& t = md.textures[i];
        std::printf("  tex[%zu] %dx%d preview=%dx%d bytes=%zu name='%s'\n", i, t.width, t.height,
                    t.previewW, t.previewH, t.preview.size(), t.name.c_str());
    }
    return 0;
}

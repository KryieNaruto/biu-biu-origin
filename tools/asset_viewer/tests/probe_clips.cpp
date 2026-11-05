#include "model_data.h"
#include <windows.h>
#include <cstdio>
int main(){
    int argc=0; LPWSTR* a=::CommandLineToArgvW(::GetCommandLineW(),&argc);
    if(!a||argc<2){std::printf("usage\n");return 2;}
    int n=::WideCharToMultiByte(CP_UTF8,0,a[1],-1,nullptr,0,nullptr,nullptr);
    std::string p((size_t)n,'\0');
    ::WideCharToMultiByte(CP_UTF8,0,a[1],-1,&p[0],n,nullptr,nullptr);
    while(!p.empty()&&p.back()=='\0') p.pop_back();
    ::LocalFree(a);
    av::ModelData md;
    if(!av::LoadModel(p,&md)||!md.ok){std::printf("fail %s\n",md.error.c_str());return 1;}
    std::printf("bones=%zu clips=%zu tris=%u\n", md.bones.size(), md.clips.size(), md.triangleCount);
    for(size_t i=0;i<md.clips.size()&&i<20;++i){
        std::printf("  clip[%zu] name='%s' dur=%.3f tracks=%zu\n", i, md.clips[i].name.c_str(),
                    md.clips[i].duration, md.clips[i].tracks.size());
    }
    return 0;
}

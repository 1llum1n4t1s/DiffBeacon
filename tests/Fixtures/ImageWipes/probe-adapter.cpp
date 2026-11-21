#include <algorithm>
#include <climits>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <vector>
struct Image {
 unsigned w=0,h=0; std::vector<unsigned char> bytes;
 unsigned width() const {return w;} unsigned height() const {return h;}
 unsigned char* scanLine(unsigned y) {if(y>=h)throw std::runtime_error("scanLine out of range");return bytes.data()+y*w*4;}
};
class Probe {
public:
#include "original-mode.inc"
 int m_nImages=2, m_wipePosition=0, m_wipePosition_old=INT_MAX;
 WIPE_MODE m_wipeMode=WIPE_NONE;
 Image m_imgDiff[3];
#include "original-wipe.inc"
};
int main() {
 try {
  int count;std::cin>>count;if(count<=0)throw std::runtime_error("case count");
  for(int c=0;c<count;++c){
   Probe p;int mode,actions;std::cin>>p.m_nImages>>mode>>actions;
   if((p.m_nImages!=2&&p.m_nImages!=3)||(mode!=1&&mode!=2)||actions<=0)throw std::runtime_error("case header");
   p.m_wipeMode=static_cast<Probe::WIPE_MODE>(mode);
   std::vector<Image> inputs(p.m_nImages);unsigned w=0,h=0;
   for(auto& im:inputs){std::cin>>im.w>>im.h;if(!im.w||!im.h||im.w>64||im.h>64)throw std::runtime_error("dimensions");im.bytes.resize(im.w*im.h*4);for(auto& b:im.bytes){unsigned v;std::cin>>v;if(v>255)throw std::runtime_error("byte range");b=static_cast<unsigned char>(v);}w=std::max(w,im.w);h=std::max(h,im.h);}
   for(int pane=0;pane<p.m_nImages;++pane){auto& dst=p.m_imgDiff[pane];auto& src=inputs[pane];dst.w=w;dst.h=h;dst.bytes.resize(w*h*4,0);for(unsigned y=0;y<src.h;++y)memcpy(dst.scanLine(y),src.scanLine(y),src.w*4);}
   std::cout<<"{\"states\":[";
   for(int a=0;a<actions;++a){std::cin>>p.m_wipePosition;if(!std::cin)throw std::runtime_error("input failed");p.WipeEffect();if(a)std::cout<<",";std::cout<<"{\"position\":"<<p.m_wipePosition<<",\"oldPosition\":"<<p.m_wipePosition_old<<",\"processed\":[";
    for(int pane=0;pane<p.m_nImages;++pane){if(pane)std::cout<<",";auto& im=p.m_imgDiff[pane];std::cout<<"{\"width\":"<<im.w<<",\"height\":"<<im.h<<",\"bytes\":[";for(size_t j=0;j<im.bytes.size();++j){if(j)std::cout<<",";std::cout<<static_cast<unsigned>(im.bytes[j]);}std::cout<<"]}";}std::cout<<"]}";
   }std::cout<<"]}\n";
  }return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 2;}
}

#include <algorithm>
#include <cstring>
#include <vector>
#include <iostream>
#include <iomanip>
#include <stdexcept>
struct Image {
 struct Color {unsigned char rgbBlue,rgbGreen,rgbRed,rgbReserved;};
#include "original-color.inc"
 unsigned w=0,h=0; std::vector<unsigned char> bytes;
 unsigned width() const {return w;} unsigned height() const {return h;}
 const unsigned char* scanLine(unsigned y) const {if(y>=h) throw std::runtime_error("scanline out of range");return bytes.data()+y*w*4;}
 unsigned char* scanLine(unsigned y) {if(y>=h) throw std::runtime_error("scanline out of range");return bytes.data()+y*w*4;}
};
#include "original-types.inc"
class Probe {
public:
 using DiffBlocks=Array2D<int>;
 int m_nImages=2; unsigned m_diffBlockSize=8; double m_colorDistanceThreshold=0;
 Image m_imgPreprocessed[3]; Point<unsigned> m_offset[3];
 DiffBlocks m_diff,m_diff01,m_diff21,m_diff02; std::vector<DiffInfo> m_diffInfos;
 Image m_imgDiff[3];
#include "original-mode.inc"
 INSERTION_DELETION_DETECTION_MODE m_insertionDeletionDetectionMode=INSERTION_DELETION_DETECTION_NONE;
 std::vector<LineDiffInfo> m_lineDiffInfos;
 Image::Color m_diffColor=Image::Rgb(255,255,64),m_selDiffColor=Image::Rgb(255,64,64);
 Image::Color m_diffDeletedColor=Image::Rgb(192,192,192),m_selDiffDeletedColor=Image::Rgb(240,192,192);
 double m_diffColorAlpha=.7; int m_currentDiffIndex=-1;
#include "original-functions.inc"
};
void grid(const Probe::DiffBlocks& a) {
 std::cout<<"["; for(unsigned y=0;y<a.height();++y){if(y)std::cout<<",";std::cout<<"[";for(unsigned x=0;x<a.width();++x){if(x)std::cout<<",";std::cout<<a(x,y);}std::cout<<"]";}std::cout<<"]";
}
int main() {
 try {
  int total; std::cin>>total;
  for(int c=0;c<total;++c){
   Probe p; std::cin>>p.m_nImages>>p.m_diffBlockSize>>p.m_colorDistanceThreshold>>p.m_diffColorAlpha>>p.m_currentDiffIndex;
   for(int i=0;i<p.m_nImages;++i){auto& im=p.m_imgPreprocessed[i];std::cin>>im.w>>im.h;im.bytes.resize(im.w*im.h*4);for(auto& b:im.bytes){unsigned v;std::cin>>v;if(v>255)throw std::runtime_error("byte out of range");b=static_cast<unsigned char>(v);}}
   if(!std::cin)throw std::runtime_error("input failed");
   p.InitializeDiff();
   std::cout<<"{\"pair01\":";
   int count=0;
   if(p.m_nImages==2){p.CompareImages2(0,1,p.m_diff);grid(p.m_diff);std::cout<<",\"pair21\":null,\"pair02\":null";count=p.MarkDiffIndex(p.m_diff);}
   else {p.CompareImages2(0,1,p.m_diff01);p.CompareImages2(2,1,p.m_diff21);p.CompareImages2(0,2,p.m_diff02);grid(p.m_diff01);std::cout<<",\"pair21\":";grid(p.m_diff21);std::cout<<",\"pair02\":";grid(p.m_diff02);p.Make3WayDiff(p.m_diff01,p.m_diff21,p.m_diff);count=p.MarkDiffIndex3way(p.m_diff01,p.m_diff21,p.m_diff02,p.m_diff);}
   std::cout<<",\"regionIds\":";grid(p.m_diff);
   std::cout<<",\"differenceCount\":"<<count<<",\"regions\":[";
   int conflicts=0;
   for(size_t i=0;i<p.m_diffInfos.size();++i){auto& d=p.m_diffInfos[i];if(d.op==OP_DIFF)++conflicts;if(i)std::cout<<",";std::cout<<"{\"id\":"<<i+1<<",\"op\":"<<d.op<<",\"left\":"<<d.rc.left<<",\"top\":"<<d.rc.top<<",\"right\":"<<d.rc.right<<",\"bottom\":"<<d.rc.bottom<<"}";}
   if(p.m_currentDiffIndex==-2)p.m_currentDiffIndex=count-1;
   auto size=p.GetMaxWidthHeight();
   std::cout<<"],\"conflictCount\":"<<conflicts<<",\"currentDiffIndex\":"<<p.m_currentDiffIndex<<",\"processed\":[";
   for(int pane=0;pane<p.m_nImages;++pane){
    auto& src=p.m_imgPreprocessed[pane];auto& dst=p.m_imgDiff[pane];dst.w=size.cx;dst.h=size.cy;dst.bytes.resize(dst.w*dst.h*4,0);
    for(unsigned y=0;y<src.height();++y)memcpy(dst.scanLine(y),src.scanLine(y),src.width()*4);
    p.MarkDiff(pane,p.m_diff);
    if(pane)std::cout<<",";
    std::cout<<"{\"width\":"<<dst.w<<",\"height\":"<<dst.h<<",\"bytes\":[";
    for(size_t j=0;j<dst.bytes.size();++j){if(j)std::cout<<",";std::cout<<static_cast<unsigned>(dst.bytes[j]);}
    std::cout<<"]}";
   }
   std::cout<<"]}\n";
  }
  return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 2;}
}

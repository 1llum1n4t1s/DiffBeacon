#include <algorithm>
#include <chrono>
#include <climits>
#include <cstring>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <vector>
struct Image {
 struct Color {unsigned char rgbBlue,rgbGreen,rgbRed,rgbReserved;};
#include "original-color.inc"
 unsigned w=0,h=0;std::vector<unsigned char> bytes;
 unsigned width()const{return w;}unsigned height()const{return h;}
 void setSize(unsigned width,unsigned height){if(!width||!height||width>64||height>64)throw std::runtime_error("canvas dimension");w=width;h=height;bytes.assign(w*h*4,0);}
 const unsigned char* scanLine(unsigned y)const{if(y>=h)throw std::runtime_error("scanline");return bytes.data()+y*w*4;}
 unsigned char* scanLine(unsigned y){if(y>=h)throw std::runtime_error("scanline");return bytes.data()+y*w*4;}
};
#include "original-types.inc"
class CImgDiffBuffer {
public:
 using DiffBlocks=Array2D<int>;
 int m_nImages=2;unsigned m_diffBlockSize=1;double m_colorDistanceThreshold=0;
 Image m_imgPreprocessed[3],m_imgDiff[3];Point<unsigned> m_offset[3];
 DiffBlocks m_diff,m_diff01,m_diff21,m_diff02;std::vector<DiffInfo> m_diffInfos;
#include "original-modes.inc"
 INSERTION_DELETION_DETECTION_MODE m_insertionDeletionDetectionMode=INSERTION_DELETION_DETECTION_NONE;
 OVERLAY_MODE m_overlayMode=OVERLAY_NONE;WIPE_MODE m_wipeMode=WIPE_NONE;
 double m_overlayAlpha=.3,m_diffColorAlpha=.7;int m_currentDiffIndex=-1,m_wipePosition=0,m_wipePosition_old=INT_MAX;
 bool m_showDifferences=false,m_blinkDifferences=false;int m_blinkInterval=1000,m_overlayAnimationInterval=1000,cacheCalls=0;
 std::vector<LineDiffInfo> m_lineDiffInfos;
 Image::Color m_diffColor=Image::Rgb(255,255,64),m_selDiffColor=Image::Rgb(255,64,64),m_diffDeletedColor=Image::Rgb(192,192,192),m_selDiffDeletedColor=Image::Rgb(240,192,192);
 void UpdateDiffTransparencyCache(){++cacheCalls;}
#include "original-functions.inc"
};
void grid(std::ostream& o,const CImgDiffBuffer::DiffBlocks& g){o<<"[";for(unsigned y=0;y<g.height();++y){if(y)o<<",";o<<"[";for(unsigned x=0;x<g.width();++x){if(x)o<<",";o<<g(x,y);}o<<"]";}o<<"]";}
void images(std::ostream& o,const Image* ims,int n){o<<"[";for(int i=0;i<n;++i){if(i)o<<",";const auto& im=ims[i];o<<"{\"width\":"<<im.w<<",\"height\":"<<im.h<<",\"bytes\":[";for(size_t j=0;j<im.bytes.size();++j){if(j)o<<",";o<<static_cast<unsigned>(im.bytes[j]);}o<<"]}";}o<<"]";}
void classification(std::ostream& o,const CImgDiffBuffer& p){o<<"{\"pair01\":";grid(o,p.m_nImages==2?p.m_diff:p.m_diff01);o<<",\"pair21\":";grid(o,p.m_diff21);o<<",\"pair02\":";grid(o,p.m_diff02);o<<",\"regionIds\":";grid(o,p.m_diff);o<<",\"regions\":[";for(size_t i=0;i<p.m_diffInfos.size();++i){if(i)o<<",";const auto& d=p.m_diffInfos[i];o<<"{\"id\":"<<i+1<<",\"op\":"<<d.op<<",\"left\":"<<d.rc.left<<",\"top\":"<<d.rc.top<<",\"right\":"<<d.rc.right<<",\"bottom\":"<<d.rc.bottom<<"}";}o<<"]}";}
int main(){try{int total;std::cin>>total;if(total<=0)throw std::runtime_error("count");for(int c=0;c<total;++c){CImgDiffBuffer p;int mode,show,wipe;std::cin>>p.m_nImages>>mode>>p.m_overlayAlpha>>show>>p.m_diffColorAlpha>>p.m_currentDiffIndex>>wipe>>p.m_wipePosition;if((p.m_nImages!=2&&p.m_nImages!=3)||mode<0||mode>2||wipe<0||wipe>2||p.m_overlayAlpha<0||p.m_overlayAlpha>1||p.m_diffColorAlpha<0||p.m_diffColorAlpha>1)throw std::runtime_error("header");
 for(int i=0;i<p.m_nImages;++i){auto& im=p.m_imgPreprocessed[i];unsigned w,h;std::cin>>w>>h>>p.m_offset[i].x>>p.m_offset[i].y;if(p.m_offset[i].x>8||p.m_offset[i].y>8)throw std::runtime_error("offset");im.setSize(w,h);for(auto& b:im.bytes){unsigned v;std::cin>>v;if(v>255)throw std::runtime_error("byte");b=static_cast<unsigned char>(v);}}
 if(!std::cin)throw std::runtime_error("input");p.InitializeDiff();if(p.m_nImages==2){p.CompareImages2(0,1,p.m_diff);p.MarkDiffIndex(p.m_diff);}else{p.CompareImages2(0,1,p.m_diff01);p.CompareImages2(2,1,p.m_diff21);p.CompareImages2(0,2,p.m_diff02);p.Make3WayDiff(p.m_diff01,p.m_diff21,p.m_diff);p.MarkDiffIndex3way(p.m_diff01,p.m_diff21,p.m_diff02,p.m_diff);}
 std::cout<<"{\"classificationBefore\":";classification(std::cout,p);std::cout<<",\"rawBefore\":";images(std::cout,p.m_imgPreprocessed,p.m_nImages);
 p.RefreshImages();std::cout<<",\"baseCanvas\":";images(std::cout,p.m_imgDiff,p.m_nImages);
 p.m_overlayMode=static_cast<CImgDiffBuffer::OVERLAY_MODE>(mode);p.m_showDifferences=show!=0;p.m_wipeMode=static_cast<CImgDiffBuffer::WIPE_MODE>(wipe);p.RefreshImages();
 std::cout<<",\"processed\":";images(std::cout,p.m_imgDiff,p.m_nImages);std::cout<<",\"classificationAfter\":";classification(std::cout,p);std::cout<<",\"rawAfter\":";images(std::cout,p.m_imgPreprocessed,p.m_nImages);std::cout<<",\"position\":"<<p.m_wipePosition<<",\"oldPosition\":"<<p.m_wipePosition_old<<",\"cacheCalls\":"<<p.cacheCalls<<"}\n";
 }return 0;}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 2;}}

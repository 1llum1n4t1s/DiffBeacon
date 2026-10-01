#include <algorithm>
#include <cassert>
#include <cstring>
#include <vector>
#include <iostream>
#include <stdexcept>
#include <string>
struct Image {
 unsigned w=0,h=0;std::vector<unsigned char> bytes;
 unsigned width()const{return w;}unsigned height()const{return h;}
 unsigned char* scanLine(unsigned y){if(y>=h)throw std::runtime_error("scanline out of range");return bytes.data()+y*w*4;}
 const unsigned char* scanLine(unsigned y)const{if(y>=h)throw std::runtime_error("scanline out of range");return bytes.data()+y*w*4;}
 void setSize(unsigned width,unsigned height){if(width==0||height==0||width*height>16000000)throw std::runtime_error("adapter canvas limit");w=width;h=height;bytes.assign(w*h*4,0);}
 bool pasteSubImage(const Image& src,unsigned x,unsigned y){if(x+src.w>w||y+src.h>h)throw std::runtime_error("paste out of range");for(unsigned row=0;row<src.h;++row)memcpy(scanLine(y+row)+x*4,src.scanLine(row),src.w*4);return true;}
};
#include "original-types.inc"
#include "original-history.inc"
class CImgDiffBuffer {
public:
 using DiffBlocks=Array2D<int>;
 int m_nImages=2,m_diffCount=0;unsigned m_diffBlockSize=8;double m_colorDistanceThreshold=0;
 Image m_imgOrig32[3],m_imgPreprocessed[3];Point<unsigned> m_offset[3];
 DiffBlocks m_diff,m_diff01,m_diff21,m_diff02;std::vector<DiffInfo> m_diffInfos;
 bool m_bRO[3]={false,false,false},m_temporarilyTransformed=false;UndoRecords m_undoRecords;
#include "original-mode.inc"
 INSERTION_DELETION_DETECTION_MODE m_insertionDeletionDetectionMode=INSERTION_DELETION_DETECTION_NONE;
 std::vector<LineDiffInfo> m_lineDiffInfos;
#include "original-comparison.inc"
#include "original-convert.inc"
#include "original-transformation.inc"
#include "original-copy.inc"
 int GetImageWidth(int pane)const{return m_imgOrig32[pane].width();}int GetImageHeight(int pane)const{return m_imgOrig32[pane].height();}
 void TransformImages(bool reverse){m_temporarilyTransformed=!reverse;}
 void InsertRows(int,int,int){throw std::runtime_error("excluded insertion");}void DeleteRows(int,int,int){throw std::runtime_error("excluded deletion");}
 void InsertColumns(int,int,int){throw std::runtime_error("excluded insertion");}void DeleteColumns(int,int,int){throw std::runtime_error("excluded deletion");}
 void CompareImages(){for(int i=0;i<m_nImages;++i)m_imgPreprocessed[i]=m_imgOrig32[i];InitializeDiff();if(m_nImages==2){CompareImages2(0,1,m_diff);m_diffCount=MarkDiffIndex(m_diff);}else{CompareImages2(0,1,m_diff01);CompareImages2(2,1,m_diff21);CompareImages2(0,2,m_diff02);Make3WayDiff(m_diff01,m_diff21,m_diff);m_diffCount=MarkDiffIndex3way(m_diff01,m_diff21,m_diff02,m_diff);}}
};
void grid(const CImgDiffBuffer::DiffBlocks& a){std::cout<<"[";for(unsigned y=0;y<a.height();++y){if(y)std::cout<<",";std::cout<<"[";for(unsigned x=0;x<a.width();++x){if(x)std::cout<<",";std::cout<<a(x,y);}std::cout<<"]";}std::cout<<"]";}
void state(CImgDiffBuffer& p){
 std::cout<<"{\"frames\":[";
 for(int i=0;i<p.m_nImages;++i){if(i)std::cout<<",";auto& im=p.m_imgOrig32[i];std::cout<<"{\"width\":"<<im.w<<",\"height\":"<<im.h<<",\"bytes\":[";for(size_t j=0;j<im.bytes.size();++j){if(j)std::cout<<",";std::cout<<static_cast<unsigned>(im.bytes[j]);}std::cout<<"]}";}
 std::cout<<"],\"regionIds\":";grid(p.m_diff);std::cout<<",\"differenceCount\":"<<p.m_diffCount<<",\"regions\":[";
 int conflicts=0;for(size_t i=0;i<p.m_diffInfos.size();++i){auto& d=p.m_diffInfos[i];if(d.op==OP_DIFF)++conflicts;if(i)std::cout<<",";std::cout<<"{\"id\":"<<i+1<<",\"op\":"<<d.op<<",\"left\":"<<d.rc.left<<",\"top\":"<<d.rc.top<<",\"right\":"<<d.rc.right<<",\"bottom\":"<<d.rc.bottom<<"}";}
 std::cout<<"],\"conflictCount\":"<<conflicts<<",\"history\":{\"index\":"<<p.m_undoRecords.m_currentUndoBufIndex<<",\"count\":"<<p.m_undoRecords.m_undoBuf.size()<<",\"undoable\":"<<(p.IsUndoable()?"true":"false")<<",\"redoable\":"<<(p.IsRedoable()?"true":"false")<<",\"panes\":[";
 for(int i=0;i<p.m_nImages;++i){if(i)std::cout<<",";std::cout<<"{\"modified\":"<<(p.IsModified(i)?"true":"false")<<",\"modcount\":"<<p.m_undoRecords.m_modcount[i]<<",\"savepoint\":"<<p.GetSavePoint(i)<<"}";}
 std::cout<<"]}}";
}
int main(){try{int total;std::cin>>total;for(int c=0;c<total;++c){CImgDiffBuffer p;std::cin>>p.m_nImages>>p.m_diffBlockSize>>p.m_colorDistanceThreshold;for(int i=0;i<p.m_nImages;++i){auto& im=p.m_imgOrig32[i];int ro;std::cin>>im.w>>im.h>>ro;p.m_bRO[i]=ro!=0;im.bytes.resize(im.w*im.h*4);for(auto& b:im.bytes){unsigned v;std::cin>>v;b=static_cast<unsigned char>(v);}}
 p.CompareImages();int actions;std::cin>>actions;std::cout<<"{\"states\":[";state(p);
 for(int a=0;a<actions;++a){std::string kind;int src,dst,index;std::cin>>kind>>src>>dst>>index;if(!std::cin)throw std::runtime_error("input failed");int result=-1;
 if(kind=="copy")p.CopyDiff(index,src,dst);else if(kind=="all")p.CopyDiffAll(src,dst);else if(kind=="auto")result=p.CopyDiff3Way(dst);else if(kind=="undo")result=p.Undo();else if(kind=="redo")result=p.Redo();else if(kind=="save")p.m_undoRecords.save(dst);else if(kind=="set-savepoint")p.SetSavePoint(dst,index);else throw std::runtime_error("unknown action");
 std::cout<<",{"<<"\"actionResult\":"<<result<<",\"state\":";state(p);std::cout<<"}";
 }std::cout<<"]}\n";}return 0;}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 2;}}

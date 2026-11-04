using System;
using System.Text;

namespace UntamedLab
{
    // A real FAT12 floppy image containing exactly Autounattend.xml.
    // No host disks are opened or mounted. This helper only returns bytes.
    public static class LabAnswerMedia
    {
        const int Sector=512, ImageBytes=1474560, FatBytes=4608, Root=9728, Data=16896;
        static readonly byte[] ShortName=Encoding.ASCII.GetBytes("AUTOUN~1XML");
        static readonly int[] LongOffsets={1,3,5,7,9,14,16,18,20,22,24,28,30};
        static void U16(byte[] image,int at,int value){image[at]=(byte)value;image[at+1]=(byte)(value>>8);}
        static void U32(byte[] image,int at,uint value)
        {for(int i=0;i<4;i++)image[at+i]=(byte)(value>>(i*8));}
        static uint Read32(byte[] image,int at)
        {return (uint)(image[at]|image[at+1]<<8|image[at+2]<<16|image[at+3]<<24);}
        static void FatEntry(byte[] fat,int cluster,int next)
        {
            int at=cluster+cluster/2;
            if((cluster&1)==0){fat[at]=(byte)next;fat[at+1]=(byte)((fat[at+1]&0xF0)|((next>>8)&0x0F));}
            else{fat[at]=(byte)((fat[at]&0x0F)|((next<<4)&0xF0));fat[at+1]=(byte)(next>>4);}
        }
        public static byte[] Create(byte[] answerXml)
        {
            if(answerXml==null||answerXml.Length<1||answerXml.Length>131072)
                throw new ArgumentException("Answer XML must be 1-128 KiB");
            byte[] image=new byte[ImageBytes];
            image[0]=0xEB;image[1]=0x3C;image[2]=0x90;
            Array.Copy(Encoding.ASCII.GetBytes("UNTAMED "),0,image,3,8);
            U16(image,11,Sector);image[13]=1;U16(image,14,1);image[16]=2;
            U16(image,17,224);U16(image,19,2880);image[21]=0xF0;U16(image,22,9);
            U16(image,24,18);U16(image,26,2);image[38]=0x29;U32(image,39,0x554E544D);
            Array.Copy(Encoding.ASCII.GetBytes("UNTAMEDLAB "),0,image,43,11);
            Array.Copy(Encoding.ASCII.GetBytes("FAT12   "),0,image,54,8);
            image[510]=0x55;image[511]=0xAA;
            byte[] fat=new byte[FatBytes];fat[0]=0xF0;fat[1]=0xFF;fat[2]=0xFF;
            int count=(answerXml.Length+Sector-1)/Sector;
            for(int i=0;i<count;i++)FatEntry(fat,2+i,i==count-1?0xFFF:3+i);
            Array.Copy(fat,0,image,Sector,FatBytes);Array.Copy(fat,0,image,Sector+FatBytes,FatBytes);
            int checksum=0;foreach(byte b in ShortName)checksum=(((checksum&1)<<7)+(checksum>>1)+b)&255;
            string longName="Autounattend.xml";
            for(int entry=0;entry<2;entry++)
            {
                int sequence=2-entry,at=Root+entry*32;
                image[at]=(byte)(sequence|(entry==0?0x40:0));image[at+11]=0x0F;image[at+13]=(byte)checksum;
                for(int i=0;i<13;i++)
                {
                    int index=(sequence-1)*13+i;
                    int character=index<longName.Length?longName[index]:index==longName.Length?0:0xFFFF;
                    U16(image,at+LongOffsets[i],character);
                }
            }
            int file=Root+64;Array.Copy(ShortName,0,image,file,11);image[file+11]=0x20;
            U16(image,file+24,0x5821);U16(image,file+26,2);U32(image,file+28,(uint)answerXml.Length);
            Array.Copy(answerXml,0,image,Data,answerXml.Length);return image;
        }
        public static byte[] ExtractVerified(byte[] image)
        {
            if(image==null||image.Length!=ImageBytes)throw new ArgumentException("Invalid lab answer image size");
            uint bytes=Read32(image,Root+64+28);
            if(bytes<1||bytes>131072)throw new ArgumentException("Invalid lab answer length");
            byte[] answer=new byte[bytes];Array.Copy(image,Data,answer,0,(int)bytes);
            byte[] canonical=Create(answer);
            for(int i=0;i<canonical.Length;i++)if(image[i]!=canonical[i])
                throw new ArgumentException("Modified or unrecognized lab answer media");
            return answer;
        }
    }
}

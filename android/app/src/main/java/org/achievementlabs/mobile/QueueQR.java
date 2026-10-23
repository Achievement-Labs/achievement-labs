package org.achievementlabs.mobile;
import org.json.*;import java.util.*;
final class QueueQR {
 static ArrayList<String> frames(JSONObject transfer,String owner)throws Exception{
  QueueTransfer.validate(transfer,owner);if(transfer.getJSONArray("games").length()==0)throw new Exception("Save a game queue first.");
  byte[] data=Core.utf(transfer.toString());if(data.length>128*1024)throw new Exception("These queues are too large for QR. Use a queue sync file.");
  String text=Core.b64(data),digest=Core.hash(data);int count=(text.length()+799)/800;ArrayList<String> frames=new ArrayList<>();for(int i=0;i<count;i++)frames.add("ALQ1|"+digest+"|"+i+"|"+count+"|"+text.substring(i*800,Math.min(text.length(),(i+1)*800)));return frames;
 }
}
